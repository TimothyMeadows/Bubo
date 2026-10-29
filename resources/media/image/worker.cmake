# Included only during explicit provisioning of the exact pinned native source.
add_executable(bubo-flux-worker "${CMAKE_CURRENT_LIST_DIR}/worker.cpp")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_CURRENT_LIST_DIR}/worker.cpp")
file(SHA256 "${CMAKE_CURRENT_LIST_DIR}/worker.cpp" BUBO_FLUX_WORKER_SHA256)
target_compile_definitions(bubo-flux-worker PRIVATE "BUBO_FLUX_WORKER_SHA256=\"${BUBO_FLUX_WORKER_SHA256}\"")
target_compile_features(bubo-flux-worker PRIVATE cxx_std_17)
target_link_libraries(bubo-flux-worker PRIVATE stable-diffusion CUDA::cudart)
set_target_properties(bubo-flux-worker PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
