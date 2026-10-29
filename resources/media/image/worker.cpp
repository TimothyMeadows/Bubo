// Bubo fixed FLUX worker. Compile only against the source revision in its catalog.
// One finite batch, one loaded context; no server, downloads or arbitrary callbacks.
#include "stable-diffusion.h"
#include <cuda_runtime_api.h>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
#include <set>
#ifdef _WIN32
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#endif

namespace fs=std::filesystem;
using clock_type=std::chrono::steady_clock;
static double seconds(clock_type::time_point start) { return std::chrono::duration<double>(clock_type::now()-start).count(); }
static void require(bool ok,const char* message) { if(!ok)throw std::runtime_error(message); }
static void cuda_ok(cudaError_t result) { if(result!=cudaSuccess)throw std::runtime_error(cudaGetErrorString(result)); }
template<class T> static T read(std::istream& input)
{
    T value{};input.read(reinterpret_cast<char*>(&value),sizeof value);require(bool(input),"Truncated binary request");return value;
}
static std::string text(std::istream& input,uint32_t limit=32768)
{
    auto length=read<uint32_t>(input);require(length<=limit,"Unbounded input string");
    std::string value(length,'\0');input.read(value.data(),length);require(bool(input),"Truncated input string");
    require(value.find('\0')==std::string::npos,"Embedded NUL in request");return value;
}
template<class T> static void write(std::ostream& output,T value) { output.write(reinterpret_cast<const char*>(&value),sizeof value);require(bool(output),"Cannot write worker result"); }
static void text(std::ostream& output,const std::string& value) { write(output,static_cast<uint32_t>(value.size()));output.write(value.data(),value.size());require(bool(output),"Cannot write result string"); }
struct item { std::string id,prompt;uint32_t width,height,steps;int64_t seed; };
struct result { item request;std::string rgb,info,scheduler;uint32_t channels;double inference; };
struct context_delete { void operator()(sd_ctx_t* value)const { if(value)free_sd_ctx(value); } };
struct image_delete { void operator()(sd_image_generation_result_t* value)const { if(value)free_sd_image_generation_result(value); } };
static std::string numbered(size_t index,const char* suffix)
{
    std::ostringstream value;value<<std::setw(3)<<std::setfill('0')<<index<<suffix;return value.str();
}
static void native_log(sd_log_level_t,const char* message,void*) { if(message)std::cerr<<message; }
static std::vector<std::string> loaded_modules()
{
    std::vector<std::string> paths;
#ifdef _WIN32
    std::array<HMODULE,2048> modules{};DWORD bytes=0;
    require(K32EnumProcessModules(GetCurrentProcess(),modules.data(),static_cast<DWORD>(sizeof modules),&bytes)!=0 && bytes<=sizeof modules,"Cannot enumerate loaded runtime modules");
    for(size_t i=0;i<bytes/sizeof(HMODULE);i++)
    {
        std::array<wchar_t,32768> path{};auto length=GetModuleFileNameW(modules[i],path.data(),static_cast<DWORD>(path.size()));
        require(length>0 && length<path.size(),"Cannot identify loaded runtime module");
        auto size=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,path.data(),static_cast<int>(length),nullptr,0,nullptr,nullptr);require(size>0,"Invalid module path encoding");
        std::string utf8(size,'\0');WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,path.data(),static_cast<int>(length),utf8.data(),size,nullptr,nullptr);paths.push_back(std::move(utf8));
    }
#endif
    return paths;
}
static int identity()
{
    std::cout<<"{\"schemaVersion\":\"bubo-flux-worker/v1\",\"declaredWorkerSourceSha256\":\""<<BUBO_FLUX_WORKER_SHA256
        <<"\",\"stableDiffusionRevision\":\"77b72a2efed78f55de26892c7e909cee001e8b83\",\"linkedLibraryCommit\":\""<<sd_commit()<<"\",\"linkedLibraryVersion\":\""<<sd_version()<<"\",\"ggmlRevision\":\"643e8d970cd2d926a0074f5cb12a68b38078157a\",\"cutlassRevision\":\"ad7b2f5e84fcfa124cb02b91d5bd26d238c0459e\",\"samplingPerformed\":false}\n";
    return 0;
}

static int run_worker(const fs::path& request_path,const fs::path& result_path)
{
    try
    {
        uint16_t endian=1;require(*reinterpret_cast<uint8_t*>(&endian)==1,"Little-endian host required");
        require(!fs::exists(result_path),"Immutable worker result already exists");
        require(fs::file_size(request_path)<=1048576,"Worker request exceeds1MiB");
        std::ifstream input(request_path,std::ios::binary);std::array<char,8> magic{};input.read(magic.data(),magic.size());
        require(bool(input) && std::memcmp(magic.data(),"BFLX0001",8)==0,"Unsupported worker protocol");
        auto model=text(input),vae=text(input),clip=text(input),t5=text(input),output_root=text(input);
        auto count=read<uint32_t>(input);require(count<=4,"Batch exceeds4items");
        std::vector<item> items;
        for(uint32_t i=0;i<count;i++)
        {
            item value;value.id=text(input,80);value.prompt=text(input,4096);
            value.width=read<uint32_t>(input);value.height=read<uint32_t>(input);value.seed=read<int64_t>(input);value.steps=read<uint32_t>(input);
            require(!value.id.empty() && !value.prompt.empty() && value.seed>=0,"Explicit item identity, prompt and seed required");
            require(value.width>=64 && value.width<=2048 && value.height>=64 && value.height<=2048 && value.width%64==0 && value.height%64==0 && value.steps>=1 && value.steps<=4,"Unsupported schnell dimensions or steps");
            items.push_back(std::move(value));
        }
        require(input.peek()==std::char_traits<char>::eof(),"Unexpected worker request fields");
        auto output_dir=fs::u8path(output_root);require(fs::is_directory(output_dir),"Explicit staged output directory required");
        for(const auto& path:{model,vae,clip,t5})require(fs::is_regular_file(fs::u8path(path)),"A pinned component is missing");
        sd_set_log_callback(native_log,nullptr);
        // CUDA is explicitly selected. Failure is reported rather than changing provider/device.
        cuda_ok(cudaSetDevice(0));cudaDeviceProp properties{};cuda_ok(cudaGetDeviceProperties(&properties,0));
        std::ostringstream uuid;uuid<<std::hex<<std::setfill('0');for(auto b:properties.uuid.bytes)uuid<<std::setw(2)<<static_cast<unsigned>(static_cast<uint8_t>(b));
        sd_ctx_params_t params;sd_ctx_params_init(&params);
        params.diffusion_model_path=model.c_str();params.vae_path=vae.c_str();params.clip_l_path=clip.c_str();params.t5xxl_path=t5.c_str();
        params.backend="*=CUDA0,te=CPU";
        params.params_backend="";
        params.eager_load=true;
        params.auto_fit=false;
        params.conditioning_cache_size=4;
        auto loading_start=clock_type::now();
        std::unique_ptr<sd_ctx_t,context_delete> context(new_sd_ctx(&params));require(bool(context),"Pinned FLUX context failed to load");
        cuda_ok(cudaDeviceSynchronize());double loading=seconds(loading_start);
        std::string version=sd_get_model_version_name(context.get());
        std::set<std::string> modules;for(const auto& path:loaded_modules())modules.insert(path);
        std::vector<result> outputs;
        for(size_t i=0;i<items.size();i++)
        {
            const auto& item=items[i];sd_img_gen_params_t generation;sd_img_gen_params_init(&generation);
            generation.prompt=item.prompt.c_str();generation.negative_prompt="";generation.width=static_cast<int>(item.width);generation.height=static_cast<int>(item.height);
            generation.seed=item.seed;generation.batch_count=1;generation.sample_params.sample_steps=static_cast<int>(item.steps);
            generation.sample_params.guidance.txt_cfg=1;
            generation.sample_params.sample_method=str_to_sample_method("euler");
            generation.sample_params.scheduler=sd_get_default_scheduler(context.get(),generation.sample_params.sample_method);
            cuda_ok(cudaDeviceSynchronize());auto inference_start=clock_type::now();
            sd_image_generation_result_t* raw=nullptr;
            bool generated=generate_image(context.get(),&generation,&raw);
            std::unique_ptr<sd_image_generation_result_t,image_delete> images(raw);
            require(generated && images && images->count==1,"Native generation did not return exactly one requested image");
            cuda_ok(cudaDeviceSynchronize());double inference=seconds(inference_start);
            for(const auto& path:loaded_modules())modules.insert(path);
            const auto& image=images->items[0].image;
            require(image.width==item.width && image.height==item.height && (image.channel==3 || image.channel==4) && image.data,"Invalid native image dimensions or pixels");
            auto rgb=numbered(i,".rgb"),info=numbered(i,".info.json");
            require(!fs::exists(output_dir/fs::u8path(rgb)) && !fs::exists(output_dir/fs::u8path(info)),"Immutable native output already exists");
            std::ofstream pixels(output_dir/fs::u8path(rgb),std::ios::binary);
            pixels.write(reinterpret_cast<const char*>(image.data),static_cast<std::streamsize>(uint64_t(image.width)*image.height*image.channel));
            pixels.close();require(bool(pixels),"Cannot persist native pixel output");
            require(images->items[0].generation_info && std::strlen(images->items[0].generation_info)<=524288,"Missing or unbounded native generation metadata");
            std::ofstream metadata(output_dir/fs::u8path(info),std::ios::binary);metadata<<images->items[0].generation_info;metadata.close();require(bool(metadata),"Cannot persist native generation metadata");
            outputs.push_back({item,rgb,info,sd_scheduler_name(generation.sample_params.scheduler),image.channel,inference});
        }
        auto unloading_start=clock_type::now();context.reset();cuda_ok(cudaDeviceSynchronize());cuda_ok(cudaDeviceReset());double unloading=seconds(unloading_start);
        for(const auto& path:loaded_modules())modules.insert(path);
        // A zero-item request is a load/unload probe and performs no sampling.
        std::ofstream output(result_path,std::ios::binary);output.write("BFLXR001",8);
        write(output,static_cast<uint32_t>(outputs.size()));write(output,loading);write(output,unloading);
        text(output,properties.name);text(output,uuid.str());text(output,version);
        write(output,static_cast<uint32_t>(properties.major));write(output,static_cast<uint32_t>(properties.minor));
        text(output,sd_commit());write(output,static_cast<uint32_t>(modules.size()));for(const auto& path:modules)text(output,path);
        for(const auto& value:outputs)
        {
            text(output,value.request.id);text(output,value.rgb);text(output,value.info);text(output,value.scheduler);
            write(output,value.inference);write(output,value.request.width);write(output,value.request.height);write(output,value.channels);write(output,value.request.seed);write(output,value.request.steps);
        }
        output.close();require(bool(output),"Cannot finalize worker result");
        return 0;
    }
    catch(const std::exception& error) {std::cerr<<"Bubo FLUX worker failed: "<<error.what()<<'\n';return 1;}
}

#ifdef _WIN32
int wmain(int argc,wchar_t** argv)
{
    if(argc==2 && std::wstring(argv[1])==L"--identity")return identity();
    if(argc!=3) {std::cerr<<"Usage: bubo-flux-worker REQUEST.bin RESULT.bin\n";return 1;}
    return run_worker(fs::path(argv[1]),fs::path(argv[2]));
}
#else
int main(int argc,char** argv)
{
    if(argc==2 && std::string(argv[1])=="--identity")return identity();
    if(argc!=3) {std::cerr<<"Usage: bubo-flux-worker REQUEST.bin RESULT.bin\n";return 1;}
    return run_worker(fs::u8path(argv[1]),fs::u8path(argv[2]));
}
#endif
