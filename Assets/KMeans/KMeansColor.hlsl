#ifndef KMEANS_COLOR_INCLUDED
#define KMEANS_COLOR_INCLUDED

// Color modes (must match KMeansClustering.ClusterColorMode on the C# side)
#define COLOR_MODE_RGB    0
#define COLOR_MODE_LAB76  1   // CIELAB, Euclidean distance (Delta E 1976)
#define COLOR_MODE_LAB94  2   // CIELAB, Delta E 1994

uint _ColorMode;
// 1 only in a GAMMA-space project (texture values are sRGB-encoded and must be linearized).
// In a LINEAR-space project the hardware already returns linear values: converting again is
// exactly the double sRGB->linear bug from the video, so this stays 0.
uint _InputIsGamma;

static const float3 WHITE_D65 = float3(0.95047, 1.0, 1.08883);
static const float LAB_EPS = 216.0 / 24389.0;     // 0.008856
static const float LAB_KAPPA = 24389.0 / 27.0;    // 903.2963

float3 SRGBToLinear(float3 c)
{
    float3 low  = c / 12.92;
    float3 high = pow(max((c + 0.055) / 1.055, 0.0), 2.4);
    return lerp(high, low, step(c, 0.04045));
}

float3 LabF(float3 t)
{
    float3 cubeRoot = pow(max(t, 0.0), 1.0 / 3.0);
    float3 linearPart = (LAB_KAPPA * t + 16.0) / 116.0;
    return lerp(linearPart, cubeRoot, step(LAB_EPS, t));   // t > eps -> cube root
}

// Linear sRGB (D65) -> CIELAB (L: 0..100, a/b: roughly -128..127)
float3 LinearRGBToLab(float3 rgb)
{
    float3 xyz = float3(
        dot(float3(0.4124564, 0.3575761, 0.1804375), rgb),
        dot(float3(0.2126729, 0.7151522, 0.0721750), rgb),
        dot(float3(0.0193339, 0.1191920, 0.9503041), rgb));

    float3 f = LabF(xyz / WHITE_D65);
    return float3(116.0 * f.y - 16.0, 500.0 * (f.x - f.y), 200.0 * (f.y - f.z));
}

// Converts a texture color into the space K-means works in (RGB or Lab).
float3 ToWorkingSpace(float3 texColor)
{
    if (_ColorMode == COLOR_MODE_RGB)
        return texColor;

    float3 linearRGB = (_InputIsGamma != 0) ? SRGBToLinear(texColor) : texColor;
    return LinearRGBToLab(linearRGB);
}

// SQUARED distance between a pixel and a cluster center, both in the working space.
// (Same nearest cluster as the real distance, no sqrt needed.)
float ColorDistanceSq(float3 pixel, float3 cluster)
{
    float3 d = pixel - cluster;
    float dist2 = dot(d, d);

    if (_ColorMode == COLOR_MODE_LAB94)
    {
        float dL = d.x;
        float c1 = length(pixel.yz);
        float c2 = length(cluster.yz);
        float dC = c1 - c2;
        // Delta H^2 = Delta E^2 - Delta L^2 - Delta C^2. Rounding can push it slightly below 0 -> clamp.
        float dH2 = max(dist2 - dL * dL - dC * dC, 0.0);

        float sc = 1.0 + 0.045 * c1;   // graphic-arts weights, kL = kC = kH = 1, sL = 1
        float sh = 1.0 + 0.015 * c1;

        return dL * dL + (dC * dC) / (sc * sc) + dH2 / (sh * sh);
    }

    return dist2;   // RGB or Lab76
}

#endif
