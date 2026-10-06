#ifndef KMEANS_COMMON_INCLUDED
#define KMEANS_COMMON_INCLUDED

#include "KMeansColor.hlsl"

// Must match KMeansClustering.PixelThreadSize on the C# side.
#define CLUSTERING_THREAD_SIZE 8

// ---- Shared resources (names must match the strings used in KMeansClustering.cs) ----
RWStructuredBuffer<float3> _Pixels; // every pixel in the working space (RGB or Lab), filled once per run
RWStructuredBuffer<float3> _Clusters; // cluster centers, in the working space
RWStructuredBuffer<int> _ClusterIndeces; // cluster index of every pixel

uint _NumClusters;
uint _Width;
uint _Height;

uint GetTotalPixels()
{
    return _Width * _Height;
}

// Converts a 2D thread id into a linear pixel index. Returns false if outside the image.
bool GetPixelID(uint3 id, out uint pixelID)
{
    pixelID = 0;
    if (id.x >= _Width || id.y >= _Height)
        return false;

    pixelID = id.y * _Width + id.x;
    return true;
}

// Color of a pixel in the working space.
float3 GetPixel(uint pixelID)
{
    return _Pixels[pixelID];
}

#endif
