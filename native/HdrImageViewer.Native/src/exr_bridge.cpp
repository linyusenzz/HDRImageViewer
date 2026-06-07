#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <algorithm>
#include <cmath>
#include <string>
#include <vector>

#if defined(HDRIMAGEVIEWER_HAS_OPENEXR)
#include <OpenEXR/ImfRgbaFile.h>
#include <OpenEXR/ImfTiledRgbaFile.h>
#include <OpenEXR/ImfRgba.h>
#include <OpenEXR/ImfFrameBuffer.h>
#include <OpenEXR/ImfHeader.h>
#include <OpenEXR/ImfTestFile.h>
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

void copy_rgba_to_half_output(
    const std::vector<Imf::Rgba>& rgba,
    int32_t width,
    int32_t height,
    uint16_t* output)
{
    for (int32_t y = 0; y < height; y++)
    {
        for (int32_t x = 0; x < width; x++)
        {
            const auto source = static_cast<std::size_t>(y) * width + x;
            const auto destination = source * 4;
            output[destination] = half_bits(rgba[source].r);
            output[destination + 1] = half_bits(rgba[source].g);
            output[destination + 2] = half_bits(rgba[source].b);
            output[destination + 3] = half_bits(rgba[source].a);
        }
    }
}

bool try_decode_tiled_preview(
    const std::string& utf8Path,
    int32_t maxPixelSize,
    HdrivExrImage* image)
{
    if (!Imf::isTiledOpenExrFile(utf8Path.c_str()))
    {
        return false;
    }

    Imf::TiledRgbaInputFile file(utf8Path.c_str());
    if (file.numXLevels() <= 1 && file.numYLevels() <= 1)
    {
        return false;
    }

    auto choose_level = [maxPixelSize](auto levelCount, auto levelSize) -> int32_t
    {
        auto best = int32_t{0};
        for (int32_t level = 0; level < levelCount; level++)
        {
            best = level;
            if (levelSize(level) <= maxPixelSize)
            {
                break;
            }
        }
        return best;
    };

    int32_t lx = 0;
    int32_t ly = 0;
    if (file.levelMode() == Imf::RIPMAP_LEVELS)
    {
        lx = choose_level(file.numXLevels(), [&file](int32_t level) { return file.levelWidth(level); });
        ly = choose_level(file.numYLevels(), [&file](int32_t level) { return file.levelHeight(level); });
    }
    else
    {
        const auto levelCount = file.numLevels();
        auto best = int32_t{0};
        for (int32_t level = 0; level < levelCount; level++)
        {
            best = level;
            if (std::max(file.levelWidth(level), file.levelHeight(level)) <= maxPixelSize)
            {
                break;
            }
        }
        lx = best;
        ly = best;
    }

    if (!file.isValidLevel(lx, ly))
    {
        return false;
    }

    const auto levelWindow = file.dataWindowForLevel(lx, ly);
    const auto width = levelWindow.max.x - levelWindow.min.x + 1;
    const auto height = levelWindow.max.y - levelWindow.min.y + 1;
    if (width <= 0 || height <= 0)
    {
        return false;
    }

    std::vector<Imf::Rgba> rgba(static_cast<std::size_t>(width) * static_cast<std::size_t>(height));
    file.setFrameBuffer(
        rgba.data() - levelWindow.min.x - (levelWindow.min.y * width),
        1,
        static_cast<std::size_t>(width));
    file.readTiles(0, file.numXTiles(lx) - 1, 0, file.numYTiles(ly) - 1, lx, ly);

    const auto sampleCount = static_cast<std::size_t>(width) * static_cast<std::size_t>(height) * 4;
    auto* output = static_cast<uint16_t*>(std::malloc(sampleCount * sizeof(uint16_t)));
    if (output == nullptr)
    {
        throw std::bad_alloc();
    }

    copy_rgba_to_half_output(rgba, width, height, output);
    image->width = width;
    image->height = height;
    image->rgbaHalfPixels = output;
    return true;
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

HDRIV_EXPORT int32_t hdriv_exr_decode_rgba16f_preview(
    const wchar_t* path,
    int32_t maxPixelSize,
    HdrivExrImage* image,
    wchar_t* errorBuffer,
    int32_t errorBufferLength)
{
    clear_image(image);

#if defined(HDRIMAGEVIEWER_HAS_OPENEXR)
    if (maxPixelSize <= 0)
    {
        return hdriv_exr_decode_rgba16f(path, image, errorBuffer, errorBufferLength);
    }

    if (path == nullptr || image == nullptr)
    {
        write_error(errorBuffer, errorBufferLength, L"Invalid argument passed to EXR preview decoder.");
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

        if (try_decode_tiled_preview(utf8Path, maxPixelSize, image))
        {
            return 0;
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

        const auto largerSide = std::max(width, height);
        if (largerSide <= maxPixelSize)
        {
            return hdriv_exr_decode_rgba16f(path, image, errorBuffer, errorBufferLength);
        }

        const auto scale = static_cast<double>(maxPixelSize) / static_cast<double>(largerSide);
        const auto previewWidth = std::max(1, static_cast<int32_t>(std::llround(width * scale)));
        const auto previewHeight = std::max(1, static_cast<int32_t>(std::llround(height * scale)));
        const auto sampleCount = static_cast<std::size_t>(previewWidth) * static_cast<std::size_t>(previewHeight) * 4;
        auto* output = static_cast<uint16_t*>(std::malloc(sampleCount * sizeof(uint16_t)));
        if (output == nullptr)
        {
            write_error(errorBuffer, errorBufferLength, L"Out of memory while decoding EXR preview.");
            return -4;
        }

        constexpr int32_t sourceBlockRows = 64;
        auto source_offset_for_preview_y = [height, previewHeight](int32_t y) -> int32_t
        {
            return std::min<int32_t>(
                height - 1,
                static_cast<int32_t>((static_cast<int64_t>(y) * height) / previewHeight));
        };

        auto previewY = int32_t{0};
        while (previewY < previewHeight)
        {
            const auto blockStartOffset = source_offset_for_preview_y(previewY);
            const auto blockEndOffset = std::min<int32_t>(height - 1, blockStartOffset + sourceBlockRows - 1);
            const auto blockRows = blockEndOffset - blockStartOffset + 1;
            std::vector<Imf::Rgba> block(static_cast<std::size_t>(width) * static_cast<std::size_t>(blockRows));
            const auto sourceStartY = dataWindow.min.y + blockStartOffset;
            const auto sourceEndY = dataWindow.min.y + blockEndOffset;
            file.setFrameBuffer(
                block.data() - dataWindow.min.x - (sourceStartY * width),
                1,
                static_cast<std::size_t>(width));
            file.readPixels(sourceStartY, sourceEndY);

            while (previewY < previewHeight)
            {
                const auto sourceYOffset = source_offset_for_preview_y(previewY);
                if (sourceYOffset > blockEndOffset)
                {
                    break;
                }

                const auto blockRowOffset = static_cast<std::size_t>(sourceYOffset - blockStartOffset) * static_cast<std::size_t>(width);
                for (int32_t x = 0; x < previewWidth; x++)
                {
                    const auto sourceX = std::min<int32_t>(
                        width - 1,
                        static_cast<int32_t>((static_cast<int64_t>(x) * width) / previewWidth));
                    const auto& pixel = block[blockRowOffset + static_cast<std::size_t>(sourceX)];
                    const auto destination = (static_cast<std::size_t>(previewY) * previewWidth + x) * 4;
                    output[destination] = half_bits(pixel.r);
                    output[destination + 1] = half_bits(pixel.g);
                    output[destination + 2] = half_bits(pixel.b);
                    output[destination + 3] = half_bits(pixel.a);
                }

                previewY++;
            }
        }

        image->width = previewWidth;
        image->height = previewHeight;
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
        write_error(errorBuffer, errorBufferLength, L"Unknown native EXR preview decoder failure.");
        return -6;
    }
#else
    (void)path;
    (void)maxPixelSize;
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
