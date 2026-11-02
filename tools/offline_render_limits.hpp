#pragma once
#include <cstdint>

namespace offline_limits
{
    constexpr uint32_t max_dimension = 16384;
    constexpr uint64_t max_pixels = 64ull * 1024 * 1024;
    constexpr uint32_t max_preview_dimension = 8192;
    constexpr uint64_t max_preview_pixels = 16ull * 1024 * 1024;
    constexpr bool image_size(uint32_t width, uint32_t height)
    {
        return width && height && width <= max_dimension && height <= max_dimension &&
            static_cast<uint64_t>(width) * height <= max_pixels;
    }
    constexpr bool preview_size(uint32_t width, uint32_t height)
    {
        return width && height && width <= max_preview_dimension && height <= max_preview_dimension &&
            static_cast<uint64_t>(width) * height <= max_preview_pixels;
    }
}
