using UnityEngine;

/// <summary>
/// SLIC superpixels on the GPU. Usage:
///   SetImage(texture);          // once per image
///   Begin(maxSegments);         // converts to Lab and places the initial grid of centers
///   Step(); Step(); ...         // one SLIC iteration each (blur, assign pixels, move centers)
///   ApplyPalette(colors);       // each superpixel takes its nearest palette color
///   RenderCanvas();             // refreshes CanvasTexture (paint-by-numbers canvas)
/// Render() refreshes the debug textures (ColorTexture / BorderTexture).
/// </summary>
public class SuperPixelClustering : MonoBehaviour
{
    [Header("Compute shader")]
    public ComputeShader Shader;   // SuperPixels.compute

    [Header("Settings")]
    [Tooltip("Higher = color matters less and superpixels stay compact/square. Lower = they follow color edges more.")]
    [Min(0.1f)] public float ColorWeighting = 20f;
    [Range(64, 1024)] public int MaxAnalysisSize = 256;   // image is downscaled to this first
    [Tooltip("3x3 blur passes before every iteration. Removes tiny islands. 0 = off.")]
    [Range(0, 4)] public int BlurPasses = 1;

    [Header("Canvas")]
    [Tooltip("true = regions filled with their palette color. false = blank canvas (only hovered region is filled).")]
    public bool ShowColors = true;
    [Tooltip("Palette index being hovered, -1 = none.")]
    public int Highlight = -1;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int NumSuperPixels { get; private set; }
    public int Iterations { get; private set; }

    /// <summary>Every pixel painted with its superpixel's color (debug view).</summary>
    public RenderTexture ColorTexture => _colorTex;
    /// <summary>White borders between superpixels, black elsewhere (debug view).</summary>
    public RenderTexture BorderTexture => _borderTex;
    /// <summary>The paint-by-numbers canvas: palette colors, borders only where the palette color changes.</summary>
    public RenderTexture CanvasTexture => _canvasTex;
    /// <summary>One pixel per superpixel (grid x grid). Valid after BuildSuperPixelTexture().</summary>
    public RenderTexture SuperPixelTexture => _superPixelTex;

    public ComputeBuffer SuperPixelsBuffer => _superPixelsBuff;
    public ComputeBuffer IndicesBuffer => _indicesBuff;

    // Must match CLUSTERING_THREAD_SIZE and numthreads(64,1,1) in the shaders
    const int PixelThreadSize = 8;
    const int LinearThreadSize = 64;
    const int SuperPixelStride = sizeof(float) * 5;   // float2 + float3
    const int AccumStride = 6;                         // ACCUM_STRIDE in the HLSL

    int _kConvert, _kInit, _kAssign, _kClear, _kAccumulate, _kUpdate, _kBorders, _kColors;
    int _kBlur, _kCopyBlur, _kAssignPalette, _kCanvas, _kSuperPixelTex;
    bool _kernelsReady;
    bool _hasImage;
    bool _begun;
    int _numPalette;

    RenderTexture _analysisTex, _colorTex, _borderTex, _canvasTex, _superPixelTex;
    ComputeBuffer _pixelsBuff, _pixelsTempBuff, _indicesBuff;
    ComputeBuffer _superPixelsBuff, _accumBuff, _superPixelPaletteBuff;
    ComputeBuffer _paletteLabBuff, _paletteRgbBuff;

    bool EnsureKernels()
    {
        if (_kernelsReady) return true;
        if (Shader == null)
        {
            Debug.LogError("SuperPixelClustering: assign SuperPixels.compute in the Inspector.", this);
            return false;
        }

        _kConvert = Shader.FindKernel("ConvertToLab");
        _kInit = Shader.FindKernel("InitSuperPixels");
        _kAssign = Shader.FindKernel("AssignPixels");
        _kClear = Shader.FindKernel("ClearAccum");
        _kAccumulate = Shader.FindKernel("AccumulatePixels");
        _kUpdate = Shader.FindKernel("UpdateCenters");
        _kBorders = Shader.FindKernel("RenderBorders");
        _kColors = Shader.FindKernel("RenderColors");
        _kBlur = Shader.FindKernel("BlurPixels");
        _kCopyBlur = Shader.FindKernel("CopyBlurred");
        _kAssignPalette = Shader.FindKernel("AssignPalette");
        _kCanvas = Shader.FindKernel("RenderCanvas");
        _kSuperPixelTex = Shader.FindKernel("RenderSuperPixelTexture");
        _kernelsReady = true;
        return true;
    }

    /// <summary>Downscales the image and allocates the per-pixel buffers and output textures.</summary>
    public void SetImage(Texture source)
    {
        if (!EnsureKernels()) return;

        ReleaseImageResources();
        ReleaseSuperPixelResources();
        _begun = false;

        float scale = Mathf.Min(1f, (float)MaxAnalysisSize / Mathf.Max(source.width, source.height));
        Width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
        Height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

        _analysisTex = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear
        };
        _analysisTex.Create();
        Graphics.Blit(source, _analysisTex);

        _colorTex = NewOutputTexture();
        _borderTex = NewOutputTexture();
        _canvasTex = NewOutputTexture();

        int total = Width * Height;
        _pixelsBuff = new ComputeBuffer(total, sizeof(float) * 3);
        _pixelsTempBuff = new ComputeBuffer(total, sizeof(float) * 3);
        _indicesBuff = new ComputeBuffer(total, sizeof(int));

        Shader.SetInt("_Width", Width);
        Shader.SetInt("_Height", Height);
        // Linear project: the GPU already returns linear values -> do NOT linearize again.
        Shader.SetInt("_InputIsGamma", QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1 : 0);

        Shader.SetTexture(_kConvert, "_SourceTexture", _analysisTex);
        _hasImage = true;
    }

    /// <summary>Creates the initial grid of ~maxSegments superpixels (grid x grid, so the real count is the nearest square below).</summary>
    public void Begin(int maxSegments)
    {
        if (!_hasImage)
        {
            Debug.LogError("SuperPixelClustering: call SetImage() first.", this);
            return;
        }

        int grid = Mathf.Max(1, Mathf.FloorToInt(Mathf.Sqrt(Mathf.Max(1, maxSegments))));
        NumSuperPixels = grid * grid;
        int sizeX = Mathf.CeilToInt(Width / (float)grid);
        int sizeY = Mathf.CeilToInt(Height / (float)grid);

        ReleaseSuperPixelResources();
        _superPixelsBuff = new ComputeBuffer(NumSuperPixels, SuperPixelStride);
        _accumBuff = new ComputeBuffer(NumSuperPixels * AccumStride, sizeof(int));
        _superPixelPaletteBuff = new ComputeBuffer(NumSuperPixels, sizeof(int));

        Shader.SetInt("_NumSuperPixels", NumSuperPixels);
        Shader.SetInt("_GridSize", grid);
        Shader.SetInt("_SuperPixelSizeX", sizeX);
        Shader.SetInt("_SuperPixelSizeY", sizeY);

        BindBuffers();

        // Fresh Lab image every run: the blur modifies it in place.
        Shader.Dispatch(_kConvert, Groups(Width, PixelThreadSize), Groups(Height, PixelThreadSize), 1);
        Shader.Dispatch(_kInit, Groups(NumSuperPixels, LinearThreadSize), 1, 1);
        Iterations = 0;
        _begun = true;
    }

    /// <summary>One SLIC iteration: blur, assign every pixel to its nearest superpixel, then move the centers.</summary>
    public void Step()
    {
        if (!_begun) return;

        Shader.SetFloat("_ColorWeighting", ColorWeighting);

        int gx = Groups(Width, PixelThreadSize), gy = Groups(Height, PixelThreadSize);

        for (int i = 0; i < BlurPasses; i++)
        {
            Shader.Dispatch(_kBlur, gx, gy, 1);
            Shader.Dispatch(_kCopyBlur, gx, gy, 1);
        }

        Shader.Dispatch(_kAssign, gx, gy, 1);
        Shader.Dispatch(_kClear, Groups(NumSuperPixels * AccumStride, LinearThreadSize), 1, 1);
        Shader.Dispatch(_kAccumulate, gx, gy, 1);
        Shader.Dispatch(_kUpdate, Groups(NumSuperPixels, LinearThreadSize), 1, 1);
        Iterations++;
    }

    /// <summary>Redraws the debug textures (ColorTexture / BorderTexture).</summary>
    public void Render()
    {
        if (!_begun) return;

        Shader.SetTexture(_kColors, "_ColorOutput", _colorTex);
        Shader.SetTexture(_kBorders, "_BorderOutput", _borderTex);

        int gx = Groups(Width, PixelThreadSize), gy = Groups(Height, PixelThreadSize);
        Shader.Dispatch(_kColors, gx, gy, 1);
        Shader.Dispatch(_kBorders, gx, gy, 1);
    }

    /// <summary>Convenience: Begin + N x Step + Render.</summary>
    public void Run(int maxSegments, int iterations)
    {
        Begin(maxSegments);
        for (int i = 0; i < iterations; i++) Step();
        Render();
    }

    /// <summary>
    /// Gives every superpixel its nearest palette color. Colors are the UI/display (sRGB) values,
    /// e.g. KMeansClustering._palette. Call after the last Step().
    /// </summary>
    public void ApplyPalette(Color[] displayColors)
    {
        if (!_begun || displayColors == null || displayColors.Length == 0) return;

        int n = displayColors.Length;
        if (_paletteLabBuff == null || _paletteLabBuff.count != n)
        {
            _paletteLabBuff?.Release();
            _paletteRgbBuff?.Release();
            _paletteLabBuff = new ComputeBuffer(n, sizeof(float) * 3);
            _paletteRgbBuff = new ComputeBuffer(n, sizeof(float) * 3);
        }

        var lab = new Vector3[n];
        var rgb = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Color c = displayColors[i];
            lab[i] = KMeansClustering.LinearRGBToLab(c.linear);   // display colors are gamma -> linearize first
            rgb[i] = new Vector3(c.r, c.g, c.b);
        }
        _paletteLabBuff.SetData(lab);
        _paletteRgbBuff.SetData(rgb);
        _numPalette = n;

        Shader.SetInt("_NumPalette", n);
        Shader.SetBuffer(_kAssignPalette, "_SuperPixels", _superPixelsBuff);
        Shader.SetBuffer(_kAssignPalette, "_SuperPixelPalette", _superPixelPaletteBuff);
        Shader.SetBuffer(_kAssignPalette, "_PaletteLab", _paletteLabBuff);
        Shader.Dispatch(_kAssignPalette, Groups(NumSuperPixels, LinearThreadSize), 1, 1);
    }

    /// <summary>Changes only the colors the canvas draws with. Region assignment (Lab) is untouched.</summary>
    public void SetPaletteColors(Color[] displayColors)
    {
        if (_paletteRgbBuff == null || displayColors == null || displayColors.Length != _paletteRgbBuff.count) return;

        var rgb = new Vector3[displayColors.Length];
        for (int i = 0; i < rgb.Length; i++)
            rgb[i] = new Vector3(displayColors[i].r, displayColors[i].g, displayColors[i].b);
        _paletteRgbBuff.SetData(rgb);
    }

    /// <summary>Redraws CanvasTexture using ShowColors / Highlight. Cheap: call it on every hover change.</summary>
    public void RenderCanvas()
    {
        if (!_begun || _paletteRgbBuff == null) return;

        Shader.SetInt("_ShowColors", ShowColors ? 1 : 0);
        Shader.SetInt("_Highlight", Highlight);
        Shader.SetBuffer(_kCanvas, "_SuperPixelIndices", _indicesBuff);
        Shader.SetBuffer(_kCanvas, "_SuperPixelPalette", _superPixelPaletteBuff);
        Shader.SetBuffer(_kCanvas, "_PaletteRGB", _paletteRgbBuff);
        Shader.SetTexture(_kCanvas, "_CanvasOutput", _canvasTex);

        Shader.Dispatch(_kCanvas, Groups(Width, PixelThreadSize), Groups(Height, PixelThreadSize), 1);
    }

    /// <summary>
    /// Writes a grid x grid texture with one pixel per superpixel (its center color). Call after the last Step().
    /// Feed it to KMeansClustering.SetImage() so the palette is clustered from a few hundred points, not every pixel.
    /// </summary>
    public RenderTexture BuildSuperPixelTexture()
    {
        if (!_begun) return null;

        int grid = Mathf.RoundToInt(Mathf.Sqrt(NumSuperPixels));
        if (_superPixelTex == null || _superPixelTex.width != grid)
        {
            ReleaseTexture(ref _superPixelTex);
            _superPixelTex = new RenderTexture(grid, grid, 0, RenderTextureFormat.ARGB32)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            _superPixelTex.Create();
        }

        Shader.SetTexture(_kSuperPixelTex, "_SuperPixelOutput", _superPixelTex);
        Shader.Dispatch(_kSuperPixelTex, Groups(NumSuperPixels, LinearThreadSize), 1, 1);
        return _superPixelTex;
    }

    void BindBuffers()
    {
        int[] kernels = { _kConvert, _kInit, _kAssign, _kClear, _kAccumulate, _kUpdate,
                          _kBorders, _kColors, _kBlur, _kCopyBlur, _kAssignPalette, _kCanvas, _kSuperPixelTex };
        foreach (int k in kernels)
        {
            Shader.SetBuffer(k, "_Pixels", _pixelsBuff);
            Shader.SetBuffer(k, "_PixelsTemp", _pixelsTempBuff);
            Shader.SetBuffer(k, "_SuperPixels", _superPixelsBuff);
            Shader.SetBuffer(k, "_SuperPixelIndices", _indicesBuff);
            Shader.SetBuffer(k, "_SuperPixelPalette", _superPixelPaletteBuff);
            Shader.SetBuffer(k, "_Accum", _accumBuff);
        }
    }

    RenderTexture NewOutputTexture()
    {
        var rt = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        rt.Create();
        return rt;
    }

    static int Groups(int count, int groupSize) => Mathf.CeilToInt(count / (float)groupSize);

    void ReleaseSuperPixelResources()
    {
        _superPixelsBuff?.Release();
        _superPixelsBuff = null;
        _accumBuff?.Release();
        _accumBuff = null;
        _superPixelPaletteBuff?.Release();
        _superPixelPaletteBuff = null;
        _paletteLabBuff?.Release();
        _paletteLabBuff = null;
        _paletteRgbBuff?.Release();
        _paletteRgbBuff = null;
        _numPalette = 0;
        ReleaseTexture(ref _superPixelTex);
    }

    void ReleaseImageResources()
    {
        _pixelsBuff?.Release();
        _pixelsBuff = null;
        _pixelsTempBuff?.Release();
        _pixelsTempBuff = null;
        _indicesBuff?.Release();
        _indicesBuff = null;

        ReleaseTexture(ref _analysisTex);
        ReleaseTexture(ref _colorTex);
        ReleaseTexture(ref _borderTex);
        ReleaseTexture(ref _canvasTex);
        _hasImage = false;
    }

    static void ReleaseTexture(ref RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        Destroy(rt);
        rt = null;
    }

    void OnDestroy()
    {
        ReleaseSuperPixelResources();
        ReleaseImageResources();
    }
}
