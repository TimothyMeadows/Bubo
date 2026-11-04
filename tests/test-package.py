"""Verify the actual archive preserves audited files, notices and Linux apphost mode."""
from pathlib import Path,PurePosixPath
import argparse,hashlib,json,tarfile

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--archive',type=Path,required=True)
    parser.add_argument('--os',choices=['Windows','Linux'],required=True)
    args=parser.parse_args()
    with tarfile.open(args.archive,'r:gz') as archive:
        members={}
        for entry in archive.getmembers():
            if entry.isdir(): continue
            name=entry.name.removeprefix('./')
            assert entry.isfile() and not PurePosixPath(name).is_absolute() and '..' not in PurePosixPath(name).parts,'Unsafe package entry: '+entry.name
            assert name not in members,'Duplicate package entry: '+name
            members[name]=entry
        def read(name):
            assert name in members,'Missing package file: '+name
            return archive.extractfile(members[name]).read()
        def sha(name): return hashlib.sha256(read(name)).hexdigest()
        for name in ('LICENSE','README.md','ARCHITECTURE.md','CONTRIBUTING.md','docs/MEDIA.md','resources/media/catalog.json','resources/media/provision.py','resources/media/image/worker.cpp','resources/media/sfx/worker.py','resources/media/music/worker.py','resources/media/environments/music-windows.json','resources/media/environments/music-linux.json','resources/media/notices/ACE-Step-LICENSE.txt','provenance/hephaera-extraction.json','provenance/inspection-adaptation.json','notices/bubo/LICENSE','notices/bubo/IMPORT.json','notices/bubo/REUSE.md'):
            assert read(name),'Empty notice/document: '+name
        for name in ('resources/media/voice/worker.py','resources/media/voice/source-adaptations/core-init.py','resources/media/voice/source-adaptations/tokenizer-init.py','resources/media/notices/Qwen3-TTS-LICENSE.txt','resources/media/notices/Qwen3-TTS-NOTICE.txt'):
            assert read(name),'Missing/empty direct voice resource: '+name
        receipt=json.loads(read('build-info.json'))
        assert read('docs/VFX_ASSETS.md'), 'Missing VFX contract documentation'
        for name in ('Bubo.Vfx.Domain.dll', 'Bubo.Vfx.Application.dll', 'Bubo.Vfx.Infrastructure.dll'):
            assert read(name), 'Missing standalone VFX runtime assembly: '+name
        vfx=receipt['capabilities']['vfx']
        assert vfx['operations']==['validate','inspect','prepare','verify-preparation'] and vfx['packageSchema']=='bubo-vfx-package/v1'
        assert vfx['processingAvailable'] and vfx['packageSchemas']==['bubo-vfx-package/v1','bubo-vfx-package/v2']
        assert vfx['processingProfiles']==['sheet','sequence','math-mask']
        assert not any(vfx[key] for key in ('videoDecodingAvailable','hardProcessingMemoryAvailable','renderingAvailable','runtimeAcceptanceAvailable')), 'Package advertises unavailable production'
        build=receipt['capabilities']['build']
        assert sha('resources/catalog.json')==build['resourceCatalogSha256'],'Catalog identity differs from the verified build'
        catalog=json.loads(read('resources/catalog.json'))
        assert {name for name in members if name.startswith('resources/') and name!='resources/catalog.json'}==set(catalog['files']),'Package has missing/unreviewed resource additions'
        for name,expected in catalog['files'].items():
            assert sha(name)==expected,'Missing/modified audited package resource: '+name
        for name,expected in {**build['assemblies'],**build['launch']}.items():
            assert sha(name)==expected,'Modified package binary/configuration: '+name
        executable='bubo.exe' if args.os=='Windows' else 'bubo'
        assert read(executable)
        if args.os=='Linux': assert members[executable].mode & 0o111,'Linux apphost lost executable mode'
        print('PASS:',len(catalog['files']),'audited resources, launch identities, notices and',args.os,'apphost in',args.archive)

if __name__=='__main__': main()
