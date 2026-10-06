using System.Collections.Generic;
using UnityEngine;

public class KMeansClustering : MonoBehaviour
{
    /// <summary>Color space K-means works in. Values must match COLOR_MODE_* in KMeansColor.hlsl.</summary>
    public enum ClusterColorMode
    {
        Rgb = 0,    // plain RGB distance
        Lab76 = 1,  // CIELAB, Euclidean distance (Delta E 1976)
        Lab94 = 2   // CIELAB, Delta E 1994
    }

    /// <summary>Output of one K-means run.</summary>
    public class ClusterResult
    {
        public Color[] Centers;   // palette colors, ready to display
        public int[] Indices;     // cluster of every pixel (row 0 = bottom row)
        public int Iterations;
    }

    [Header("Compute shaders")]
    public ComputeShader InitClustersShader;            // also contains the ConvertToWorkingSpace kernel
    public ComputeShader UpdatePixelClustersShader;
    public ComputeShader UpdateClusterCentersShader;

    [Header("Color space")]
    [Tooltip("Rgb = plain RGB. Lab76 = CIELAB with Euclidean distance. Lab94 = CIELAB with Delta E 1994. Takes effect on the next run (move a slider).")]
    public ClusterColorMode ColorMode = ClusterColorMode.Lab76;
    [Tooltip("Palette colors closer than this (Delta E in Lab) are merged. Around 2.3 is barely noticeable.")]
    public float PaletteFilterDeltaE = 8f;

    [Header("Settings")]
    public int ClusteringMaxIterations = 100;                // hard stop against infinite loops
    [Range(64, 1024)] public int MaxAnalysisSize = 256;      // image is downscaled to this before clustering
    public int Seed = 1337;
    public List<Color> _palette = new List<Color>();

    public int Width { get; private set; }
    public int Height { get; private set; }

    // Must match the shaders: CLUSTERING_THREAD_SIZE and numthreads(64,1,1)
    const int PixelThreadSize = 8;
    const int ClusterThreadSize = 64;

    int _initClustersKernelIdx;
    int _convertKernelIdx;
    int _updatePixelClustersKernelIdx;
    int _updateClusterCentersKernelIdx;
    bool _ready;

    RenderTexture _analysisTex;
    ComputeBuffer _pixelsBuff;
    ComputeBuffer _clustersBuff;
    ComputeBuffer _clusterIndecesBuff;
    int _clustersBuffSize;

    Vector3[] clusterCenters;
    Vector3[] previousClusterCenters;
    int[] clusterIndeces;
    int[] previousClusterIndeces;

    int iterations;

    void Awake()
    {
        if (InitClustersShader == null || UpdatePixelClustersShader == null || UpdateClusterCentersShader == null)
        {
            Debug.LogError("KMeansClustering: assign the three compute shaders in the Inspector.", this);
            return;
        }

        _initClustersKernelIdx = InitClustersShader.FindKernel("InitClusters");
        _convertKernelIdx = InitClustersShader.FindKernel("ConvertToWorkingSpace");
        _updatePixelClustersKernelIdx = UpdatePixelClustersShader.FindKernel("UpdatePixelClusters");
        _updateClusterCentersKernelIdx = UpdateClusterCentersShader.FindKernel("UpdateClusterCenter");
        _ready = true;
    }

    /// <summary>Downscales the image and allocates the per-pixel buffers. Call once per loaded image.</summary>
    public void SetImage(Texture source)
    {
        ReleaseImageResources();

        float scale = Mathf.Min(1f, (float)MaxAnalysisSize / Mathf.Max(source.width, source.height));
        Width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
        Height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

        _analysisTex = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear
        };
        _analysisTex.Create();
        Graphics.Blit(source, _analysisTex);

        int total = Width * Height;
        _pixelsBuff = new ComputeBuffer(total, sizeof(float) * 3);
        _clusterIndecesBuff = new ComputeBuffer(total, sizeof(int));
        clusterIndeces = new int[total];
        previousClusterIndeces = new int[total];
    }

    /// <summary>Runs K-means with k clusters on the current image.</summary>
    public ClusterResult Run(int k)
    {
        if (!_ready || _analysisTex == null)
        {
            Debug.LogError("KMeansClustering: call SetImage() first and make sure the shaders are assigned.", this);
            return null;
        }

        k = Mathf.Max(1, k);
        EnsureClusterBuffer(k);

        SetCommon(InitClustersShader, _initClustersKernelIdx, k);
        SetCommon(InitClustersShader, _convertKernelIdx, k);
        InitClustersShader.SetTexture(_convertKernelIdx, "_SourceTexture", _analysisTex);
        InitClustersShader.SetInt("_Seed", Seed);
        SetCommon(UpdatePixelClustersShader, _updatePixelClustersKernelIdx, k);
        SetCommon(UpdateClusterCentersShader, _updateClusterCentersKernelIdx, k);

        int threadGroupsX = Mathf.CeilToInt(Width / (float)PixelThreadSize);
        int threadGroupsY = Mathf.CeilToInt(Height / (float)PixelThreadSize);
        int threadGroupsByClusterX = Mathf.CeilToInt(k / (float)ClusterThreadSize);

        // Reset convergence state so the first iteration can never look "converged"
        iterations = 0;
        for (int i = 0; i < previousClusterIndeces.Length; i++) previousClusterIndeces[i] = -1;
        for (int i = 0; i < previousClusterCenters.Length; i++) previousClusterCenters[i] = new Vector3(-9999f, -9999f, -9999f);

        // Convert the image to the working space (RGB or Lab) once
        InitClustersShader.Dispatch(_convertKernelIdx, threadGroupsX, threadGroupsY, 1);

        // Randomly select cluster centers
        InitClustersShader.Dispatch(_initClustersKernelIdx, threadGroupsByClusterX, 1, 1);

        bool converged;
        do
        {
            // Update pixel cluster assignment
            UpdatePixelClustersShader.Dispatch(_updatePixelClustersKernelIdx, threadGroupsX, threadGroupsY, 1);

            // Calculate new cluster centers
            UpdateClusterCentersShader.Dispatch(_updateClusterCentersKernelIdx, threadGroupsByClusterX, 1, 1);

            converged = DidConverge();
        }
        while (!converged);

        var centers = new Color[k];
        for (int i = 0; i < k; i++) centers[i] = ToDisplayColor(clusterCenters[i]);

        return new ClusterResult
        {
            Centers = centers,
            Indices = (int[])clusterIndeces.Clone(),
            Iterations = iterations
        };
    }

    void SetCommon(ComputeShader cs, int kernel, int k)
    {
        cs.SetBuffer(kernel, "_Pixels", _pixelsBuff);
        cs.SetBuffer(kernel, "_Clusters", _clustersBuff);
        cs.SetBuffer(kernel, "_ClusterIndeces", _clusterIndecesBuff);
        cs.SetInt("_NumClusters", k);
        cs.SetInt("_Width", Width);
        cs.SetInt("_Height", Height);
        cs.SetInt("_ColorMode", (int)ColorMode);
        // Linear project: the GPU already returns linear values -> do NOT linearize again.
        cs.SetInt("_InputIsGamma", QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1 : 0);
    }

    void EnsureClusterBuffer(int k)
    {
        if (_clustersBuff != null && _clustersBuffSize == k) return;

        _clustersBuff?.Release();
        _clustersBuff = new ComputeBuffer(k, sizeof(float) * 3);
        _clustersBuffSize = k;
        clusterCenters = new Vector3[k];
        previousClusterCenters = new Vector3[k];
    }

    bool DidConverge()
    {
        // Check for cluster convergence
        _clustersBuff.GetData(clusterCenters);
        bool clustersConverged = SameVectors(clusterCenters, previousClusterCenters);
        System.Array.Copy(clusterCenters, previousClusterCenters, clusterCenters.Length);

        // Check for index convergence
        _clusterIndecesBuff.GetData(clusterIndeces);
        bool indecesConverged = SameInts(clusterIndeces, previousClusterIndeces);
        System.Array.Copy(clusterIndeces, previousClusterIndeces, clusterIndeces.Length);

        // Hard stop
        iterations++;

        return iterations >= ClusteringMaxIterations || clustersConverged || indecesConverged;
    }

    static bool SameInts(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    static bool SameVectors(Vector3[] a, Vector3[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].x != b[i].x || a[i].y != b[i].y || a[i].z != b[i].z) return false;
        return true;
    }

    // ---------------------------------------------------------------------
    // Cluster value (working space) -> color for the UI (gamma / sRGB values)
    // ---------------------------------------------------------------------
    Color ToDisplayColor(Vector3 v)
    {
        if (ColorMode == ClusterColorMode.Rgb)
        {
            var c = new Color(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y), Mathf.Clamp01(v.z), 1f);
            // Linear project: GPU values were linear. Gamma project: they were already sRGB values.
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? c.gamma : c;
        }

        // Lab always goes through LINEAR RGB (on the GPU we linearized first if the project is Gamma),
        // so the UI value is always linear -> sRGB.
        return LabToLinearRGB(v).gamma;
    }

    // ---- CIELAB (D65), same maths as KMeansColor.hlsl ----
    const float LabEps = 216f / 24389f;
    const float LabKappa = 24389f / 27f;
    const float WhiteX = 0.95047f, WhiteY = 1f, WhiteZ = 1.08883f;

    static float LabF(float t) { return t > LabEps ? Mathf.Pow(t, 1f / 3f) : (LabKappa * t + 16f) / 116f; }

    static float LabFInv(float f)
    {
        float f3 = f * f * f;
        return f3 > LabEps ? f3 : (116f * f - 16f) / LabKappa;
    }

    /// <summary>Linear RGB color -> Lab (x = L, y = a, z = b).</summary>
    public static Vector3 LinearRGBToLab(Color c)
    {
        float x = 0.4124564f * c.r + 0.3575761f * c.g + 0.1804375f * c.b;
        float y = 0.2126729f * c.r + 0.7151522f * c.g + 0.0721750f * c.b;
        float z = 0.0193339f * c.r + 0.1191920f * c.g + 0.9503041f * c.b;

        float fx = LabF(x / WhiteX), fy = LabF(y / WhiteY), fz = LabF(z / WhiteZ);
        return new Vector3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    /// <summary>Lab -> linear RGB, clamped to the displayable 0..1 range.</summary>
    public static Color LabToLinearRGB(Vector3 lab)
    {
        float fy = (lab.x + 16f) / 116f;
        float fx = fy + lab.y / 500f;
        float fz = fy - lab.z / 200f;

        float x = WhiteX * LabFInv(fx);
        float y = WhiteY * LabFInv(fy);
        float z = WhiteZ * LabFInv(fz);

        float r = 3.2404542f * x - 1.5371385f * y - 0.4985314f * z;
        float g = -0.9692660f * x + 1.8760108f * y + 0.0415560f * z;
        float b = 0.0556434f * x - 0.2040259f * y + 1.0572252f * z;
        return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
    }

    /// <summary>Builds _palette: merges colors closer than PaletteFilterDeltaE and sorts by lightness (L*).</summary>
    public void UpdatePalette(Color[] clusters)
    {
        _palette.Clear();
        if (clusters == null || clusters.Length == 0) return;

        var labs = new List<Vector3>();
        var entries = new List<KeyValuePair<float, Color>>();

        foreach (Color display in clusters)
        {
            Vector3 lab = LinearRGBToLab(display.linear);   // display colors are gamma -> linearize first

            bool isTooSimilar = false;
            foreach (Vector3 other in labs)
            {
                if (Vector3.Distance(lab, other) < PaletteFilterDeltaE) { isTooSimilar = true; break; }
            }
            if (isTooSimilar) continue;

            labs.Add(lab);
            entries.Add(new KeyValuePair<float, Color>(lab.x, display));
        }

        entries.Sort((a, b) => a.Key.CompareTo(b.Key));
        foreach (var e in entries) _palette.Add(e.Value);
    }

    void ReleaseImageResources()
    {
        _pixelsBuff?.Release();
        _pixelsBuff = null;
        _clusterIndecesBuff?.Release();
        _clusterIndecesBuff = null;

        if (_analysisTex != null)
        {
            _analysisTex.Release();
            Destroy(_analysisTex);
            _analysisTex = null;
        }
    }

    void OnDestroy()
    {
        // Always release ComputeBuffers to prevent memory leaks
        _clustersBuff?.Release();
        ReleaseImageResources();
    }
}
