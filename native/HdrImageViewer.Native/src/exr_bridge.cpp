#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <string>
#include <vector>

#if defined(HDRIMAGEVIEWER_HAS_OPENEXR)
#include <OpenEXR/ImfRgbaFile.h>
#include <OpenEXR/ImfRgba.h>
#include <OpenEXR/ImfFrameBuffer.h>
#include <OpenEXR/ImfHeader.h>
#include <Imath/half.h>
#endif

#if defined(_WIN32)
#include <windows.h>
#endif

#if defined(_WIN32)
#define HDRIV_EXPORT extern "C" __declspec(dllexport)
#else
#define HDRIV_EXPORT extern "C"
#endif

struct HdrivExrImage
{
    int32_t width;
    int32_t height;
    uint16_t* rgbaHalfPixels;
};

namespace
{
void write_error(wchar_t* errorBuffer, int32_t errorBufferLength, const std::wstring& message)
{
    if (errorBuffer == nullptr || errorBufferLength <= 0)
    {
        return;
    }

    const auto copyLength = static_cast<int32_t>(std::min<std::size_t>(
        message.size(),
        static_cast<std::size_t>(errorBufferLength - 1)));
    if (copyLength > 0)
    {
        std::wmemcpy(errorBuffer, message.c_str(), static_cast<std::size_t>(copyLength));
    }

    errorBuffer[copyLength] = L'\0';
}

void clear_image(HdrivExrImage* image)
{
    if (image == nullptr)
    {
        return;
    }

    image->width = 0;
    image->height = 0;
    image->rgbaHalfPixels = nullptr;
}

#if defined(_WIN32)
std::string wide_to_utf8(const wchar_t* value)
{
    if (value == nullptr)
    {
        return {};
    }

    const auto length = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (length <= 0)
    {
        return {};
    }

    std::string result(static_cast<std::size_t>(length - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), length, nullptr, nullptr);
    return result;
}
#endif

std::wstring widen_ascii(const char* value)
{
    if (value == nullptr)
    {
        return L"Unknown native EXR error.";
    }

    std::wstring result;
    while (*value != '\0')
    {
        result.push_back(static_cast<unsigned char>(*value));
        value++;
    }

    return result;
}

#if defined(HDRIMAGEVIEWER_HAS_OPENEXR)
uint16_t half_bits(const half& value)
{
    return value.bits();
}

half half_from_bits(uint16_t value)
{
    half result;
    result.setBits(value);
    return result;
}
#endif
}

HDRIV_EXPORT int32_t hdriv_exr_decode_rgba16f(
    const wchar_t* path,
    HdrivExrImage* image,
    wchar_t* errorBuffer,
    int32_t errorBufferLength)
{
    clear_image(image);

#if defined(HDRIMAGEVIEWER_HAS_OPENEXR)
    if (path == nullptr || image == nullptr)
    {
        write_error(errorBuffer, errorBufferLength, L"Invalid argument passed to EXR decoder.");
        return -1;
    }

    try
    {
#if defined(_WIN32)
        const auto utf8Path = wide_to_utf8(path);
#else
        const std::string utf8Path = path;
#endif
        if (utf8Path.empty())
        {
            write_error(errorBuffer, errorBufferLength, L"EXR path is empty or could not be converted to UTF-8.");
            return -1;
        }

        Imf::RgbaInputFile file(utf8Path.c_str());
        const auto dataWindow = file.dataWindow();
        const auto width = dataWindow.max.x - dataWindow.min.x + 1;
        const auto height = dataWindow.max.y - dataWindow.min.y + 1;
        if (width <= 0 || height <= 0)
        {
            write_error(errorBuffer, errorBufferLength, L"EXR data window is empty.");
            return -3;
        }

        std::vector<Imf::Rgba> rgba(static_cast<std::size_t>(width) * static_cast<std::size_t>(height));
        file.setFrameBuffer(
            rgba.data() - dataWindow.min.x - (dataWindow.min.y * width),
            1,
            static_cast<std::size_t>(width));
        file.readPixels(dataWindow.min.y, dataWindow.max.y);

        const auto sampleCount = static_cast<std::size_t>(width) * static_cast<std::size_t>(height) * 4;
        auto* output = static_cast<uint16_t*>(std::malloc(sampleCount * sizeof(uint16_t)));
        if (output == nullptr)
        {
            write_error(errorBuffer, errorBufferLength, L"Out of memory while decoding EXR.");
            return -4;
        }

        for (std::size_t i = 0, j = 0; i < rgba.size(); i++, j += 4)
        {
            output[j] = half_bits(rgba[i].r);
            output[j + 1] = half_bits(rgba[i].g);
            output[j + 2] = half_bits(rgba[i].b);
            output[j + 3] = half_bits(rgba[i].a);
        }

        image->width = width;
        image->height = height;
        image->rgbaHalfPixels = output;
        return 0;
    }
    catch (const std::exception& ex)
    {
        write_error(errorBuffer, errorBufferLength, widen_ascii(ex.what()));
        return -5;
    }
    catch (...)
    {
        write_error(errorBuffer, errorBufferLength, L"Unknown native EXR decoder failure.");
        return -6;
    }
#else
    (void)path;
    if (errorBuffer != nullptr && errorBufferLength > 0)
    {
        const wchar_t message[] = L"HdrImageViewer.Native was built without OpenEXR support.";
        auto index = 0;
        while (index + 1 < errorBufferLength && message[index] != L'\0')
        {
            errorBuffer[index] = message[index];
            index++;
        }

        errorBuffer[index] = L'\0';
    }

    return -2;
#endif
}

HDRIV_EXPORT int32_t hdriv_exr_encode_rgba16f(
    const wchar_t* path,
    int32_t width,
    int32_t height,
    const uint16_t* rgbaHalfPixels,
    wchar_t* errorBuffer,
    int32_t errorBufferLength)
{
#if defined(HDRIMAGEVIEWER_HAS_OPENEXR)
    if (path == nullptr || rgbaHalfPixels == nullptr || width <= 0 || height <= 0)
    {
        write_error(errorBuffer, errorBufferLength, L"Invalid argument passed to EXR encoder.");
        return -1;
    }

    try
    {
#if defined(_WIN32)
        const auto utf8Path = wide_to_utf8(path);
#else
        const std::string utf8Path = path;
#endif
        if (utf8Path.empty())
        {
            write_error(errorBuffer, errorBufferLength, L"EXR output path is empty or could not be converted to UTF-8.");
            return -1;
        }

        std::vector<Imf::Rgba> rgba(static_cast<std::size_t>(width) * static_cast<std::size_t>(height));
        for (std::size_t i = 0, j = 0; i < rgba.size(); i++, j += 4)
        {
            rgba[i].r = half_from_bits(rgbaHalfPixels[j]);
            rgba[i].g = half_from_bits(rgbaHalfPixels[j + 1]);
            rgba[i].b = half_from_bits(rgbaHalfPixels[j + 2]);
            rgba[i].a = half_from_bits(rgbaHalfPixels[j + 3]);
        }

        Imf::RgbaOutputFile file(utf8Path.c_str(), width, height, Imf::WRITE_RGBA);
        file.setFrameBuffer(rgba.data(), 1, static_cast<std::size_t>(width));
        file.writePixels(height);
        return 0;
    }
    catch (const std::exception& ex)
    {
        write_error(errorBuffer, errorBufferLength, widen_ascii(ex.what()));
        return -5;
    }
    catch (...)
    {
        write_error(errorBuffer, errorBufferLength, L"Unknown native EXR encoder failure.");
        return -6;
    }
#else
    (void)path;
    (void)width;
    (void)height;
    (void)rgbaHalfPixels;
    if (errorBuffer != nullptr && errorBufferLength > 0)
    {
        const wchar_t message[] = L"HdrImageViewer.Native was built without OpenEXR support.";
        auto index = 0;
        while (index + 1 < errorBufferLength && message[index] != L'\0')
        {
            errorBuffer[index] = message[index];
            index++;
        }

        errorBuffer[index] = L'\0';
    }

    return -2;
#endif
}

HDRIV_EXPORT void hdriv_exr_free(void* pointer)
{
    std::free(pointer);
}
