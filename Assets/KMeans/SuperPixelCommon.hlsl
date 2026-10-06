#ifndef SUPERPIXEL_COMMON_INCLUDED
#define SUPERPIXEL_COMMON_INCLUDED

// Reuses _Pixels, _Width, _Height, GetPixelID, GetPixel, SRGBToLinear, LinearRGBToLab,
// _InputIsGamma, CLUSTERING_THREAD_SIZE and the Lab constants from the K-means files.
#include "KMeansCommon.hlsl"

struct SuperPixel
{
    float2 coord; // center position in pixels
    float3 color; // center color in CIELAB
};

RWStructuredBuffer<SuperPixel> _SuperPixels;
RWStructuredBuffer<int> _SuperPixelIndices; // superpixel index of every pixel
RWStructuredBuffer<int> _Accum; // 6 ints per superpixel: L, a, b (fixed point), x, y, count

uint _NumSuperPixels;
uint _GridSize; // superpixels per row/column (_NumSuperPixels = _GridSize^2)
int _SuperPixelSizeX;
int _SuperPixelSizeY;
float _ColorWeighting; // bigger = color matters less, proximity matters more

RWStructuredBuffer<float3> _PixelsTemp; // scratch copy used by the blur
StructuredBuffer<float3> _PaletteLab; // palette colors in CIELAB
StructuredBuffer<float3> _PaletteRGB; // palette colors as the UI shows them (sRGB values)
RWStructuredBuffer<int> _SuperPixelPalette; // palette index of every superpixel

uint _NumPalette;
int _ShowColors; // 1 = fill regions with their palette color, 0 = blank canvas
int _Highlight; // palette index being hovered, -1 = none

#define ACCUM_STRIDE 6
#define COLOR_FIXED_SCALE 64.0   // Lab -> fixed point for InterlockedAdd

SuperPixel NewSuperPixel(float2 coord, float3 color)
{
    SuperPixel sp;
    sp.coord = coord;
    sp.color = color;
    return sp;
}

bool GetSuperPixelID(uint3 id, out uint superPixelID)
{
    superPixelID = id.x;
    return id.x < _NumSuperPixels;
}

// ---- Lab -> display helpers (only used by the visualisation kernel) ----
float3 LabFInv(float3 f)
{
    float3 f3 = f * f * f;
    return lerp((116.0 * f - 16.0) / LAB_KAPPA, f3, step(LAB_EPS, f3));
}

float3 LabToLinearRGB(float3 lab)
{
    float fy = (lab.x + 16.0) / 116.0;
    float fx = fy + lab.y / 500.0;
    float fz = fy - lab.z / 200.0;

    float3 xyz = WHITE_D65 * LabFInv(float3(fx, fy, fz));

    float3 rgb = float3(
        dot(float3(3.2404542, -1.5371385, -0.4985314), xyz),
        dot(float3(-0.9692660, 1.8760108, 0.0415560), xyz),
        dot(float3(0.0556434, -0.2040259, 1.0572252), xyz));

    return saturate(rgb);
}

float3 LinearToSRGB(float3 c)
{
    float3 low = c * 12.92;
    float3 high = 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
    return lerp(high, low, step(c, 0.0031308));
}

#endif
#ifndef SUPERPIXEL_COMMON_INCLUDED
#define SUPERPIXEL_COMMON_INCLUDED

// Reuses _Pixels, _Width, _Height, GetPixelID, GetPixel, SRGBToLinear, LinearRGBToLab,
// _InputIsGamma, CLUSTERING_THREAD_SIZE and the Lab constants from the K-means files.
#include "KMeansCommon.hlsl"

struct SuperPixel
{
    float2 coord;   // center position in pixels
    float3 color;   // center color in CIELAB
};

RWStructuredBuffer<SuperPixel> _SuperPixels;
RWStructuredBuffer<int> _SuperPixelIndices;   // superpixel index of every pixel
RWStructuredBuffer<int> _Accum;               // 6 ints per superpixel: L, a, b (fixed point), x, y, count

uint _NumSuperPixels;
uint _GridSize;            // superpixels per row/column (_NumSuperPixels = _GridSize^2)
int _SuperPixelSizeX;
int _SuperPixelSizeY;
float _ColorWeighting;     // bigger = color matters less, proximity matters more

RWStructuredBuffer<float3> _PixelsTemp;        // scratch copy used by the blur
StructuredBuffer<float3> _PaletteLab;          // palette colors in CIELAB
StructuredBuffer<float3> _PaletteRGB;          // palette colors as the UI shows them (sRGB values)
RWStructuredBuffer<int> _SuperPixelPalette;    // palette index of every superpixel

uint _NumPalette;
int _ShowColors;   // 1 = fill regions with their palette color, 0 = blank canvas
int _Highlight;    // palette index being hovered, -1 = none

#define ACCUM_STRIDE 6
#define COLOR_FIXED_SCALE 64.0   // Lab -> fixed point for InterlockedAdd

SuperPixel NewSuperPixel(float2 coord, float3 color)
{
    SuperPixel sp;
    sp.coord = coord;
    sp.color = color;
    return sp;
}

bool GetSuperPixelID(uint3 id, out uint superPixelID)
{
    superPixelID = id.x;
    return id.x < _NumSuperPixels;
}

// ---- Lab -> display helpers (only used by the visualisation kernel) ----
float3 LabFInv(float3 f)
{
    float3 f3 = f * f * f;
    return lerp((116.0 * f - 16.0) / LAB_KAPPA, f3, step(LAB_EPS, f3));
}

float3 LabToLinearRGB(float3 lab)
{
    float fy = (lab.x + 16.0) / 116.0;
    float fx = fy + lab.y / 500.0;
    float fz = fy - lab.z / 200.0;

    float3 xyz = WHITE_D65 * LabFInv(float3(fx, fy, fz));

    float3 rgb = float3(
        dot(float3( 3.2404542, -1.5371385, -0.4985314), xyz),
        dot(float3(-0.9692660,  1.8760108,  0.0415560), xyz),
        dot(float3( 0.0556434, -0.2040259,  1.0572252), xyz));

    return saturate(rgb);
}

float3 LinearToSRGB(float3 c)
{
    float3 low  = c * 12.92;
    float3 high = 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
    return lerp(high, low, step(c, 0.0031308));
}

#endif
