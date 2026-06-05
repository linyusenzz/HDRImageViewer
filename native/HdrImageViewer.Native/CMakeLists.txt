cmake_minimum_required(VERSION 3.24)

project(HdrImageViewerNative LANGUAGES CXX)

add_library(HdrImageViewer.Native SHARED
    src/exr_bridge.cpp
)

set_target_properties(HdrImageViewer.Native PROPERTIES
    PREFIX ""
)

foreach(HDRIV_CONFIG Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER "${HDRIV_CONFIG}" HDRIV_CONFIG_UPPER)
    set_target_properties(HdrImageViewer.Native PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${HDRIV_CONFIG_UPPER} "${CMAKE_BINARY_DIR}/${HDRIV_CONFIG}"
        LIBRARY_OUTPUT_DIRECTORY_${HDRIV_CONFIG_UPPER} "${CMAKE_BINARY_DIR}/${HDRIV_CONFIG}"
        ARCHIVE_OUTPUT_DIRECTORY_${HDRIV_CONFIG_UPPER} "${CMAKE_BINARY_DIR}/${HDRIV_CONFIG}"
    )
endforeach()

target_compile_features(HdrImageViewer.Native PRIVATE cxx_std_20)
target_compile_definitions(HdrImageViewer.Native PRIVATE HDRIMAGEVIEWER_NATIVE_EXPORTS)

find_package(OpenEXR CONFIG QUIET)

if(OpenEXR_FOUND)
    target_compile_definitions(HdrImageViewer.Native PRIVATE HDRIMAGEVIEWER_HAS_OPENEXR=1)
    target_link_libraries(HdrImageViewer.Native PRIVATE OpenEXR::OpenEXR)
    add_custom_command(TARGET HdrImageViewer.Native POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_RUNTIME_DLLS:HdrImageViewer.Native>
            $<TARGET_FILE_DIR:HdrImageViewer.Native>
        COMMAND_EXPAND_LISTS)
    if(MINGW)
        get_filename_component(HDRIV_MINGW_BIN_DIR "${CMAKE_CXX_COMPILER}" DIRECTORY)
        add_custom_command(TARGET HdrImageViewer.Native POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${HDRIV_MINGW_BIN_DIR}/libgcc_s_seh-1.dll"
                "${HDRIV_MINGW_BIN_DIR}/libstdc++-6.dll"
                "${HDRIV_MINGW_BIN_DIR}/libwinpthread-1.dll"
                $<TARGET_FILE_DIR:HdrImageViewer.Native>
            COMMAND_EXPAND_LISTS)
    endif()
else()
    message(WARNING "OpenEXR package not found. HdrImageViewer.Native will build a stub DLL that reports EXR decode unavailable.")
endif()
