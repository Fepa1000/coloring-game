using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// UI controller: loads an image, shows the K-means result and drives the three sliders.
///  - Colors:        K, the number of base palette colors (shown in the palette bar and as "k=").
///  - Complexity:    each base color is split into more fragments (K x Complexity clusters) = more detail.
///                   With Paint By Numbers assigned it sets the max number of segments instead.
///  - Show Original: fades the original photo on top of the result (0 = result only, 1 = original).
/// With Paint By Numbers the palette is clustered from the superpixels (SLIC into K-means), not from every pixel.
/// </summary>
public class KMeansUI : MonoBehaviour
{
    [Header("Core")]
    [SerializeField] KMeansClustering clustering;
    [Tooltip("Optional. When assigned and enabled, the Result Image shows the SLIC paint-by-numbers canvas.")]
    [SerializeField] PaintByNumbers paintByNumbers;

    [Header("Images (both stacked in the same spot)")]
    [SerializeField] RawImage resultImage;     // bottom: K-means result
    [SerializeField] RawImage originalImage;   // top: original photo, its alpha = "Show Original"
    [SerializeField] RectTransform paletteStrip;
    [SerializeField] TMP_Text kLabel;          // shows "k=10"
    [SerializeField] Texture2D defaultImage;   // optional

    [Header("Load image")]
    [SerializeField] TMP_InputField pathField;
    [SerializeField] Button loadButton;

    [Header("Sliders")]
    [SerializeField] Slider colorsSlider;
    [SerializeField] Slider complexitySlider;
    [FormerlySerializedAs("blendSlider")]      // keeps the reference you already assigned
    [SerializeField] Slider showOriginalSlider;

    [Header("Behaviour")]
    [SerializeField] float rerunDelay = 0.25f; // wait after the last slider move before re-clustering

    Texture sourceTexture;
    Texture2D loadedTexture;
    Texture2D resultTexture;
    Color32[] pixelBuffer;
    KMeansClustering.ClusterResult baseResult, fineResult;
    Coroutine pending;

    bool CanvasMode => paintByNumbers != null && paintByNumbers.isActiveAndEnabled;

    void Awake()
    {
        if (!ReferencesOk()) { enabled = false; return; }

        colorsSlider.wholeNumbers = true;
        colorsSlider.minValue = 2; colorsSlider.maxValue = 32; colorsSlider.SetValueWithoutNotify(10);

        complexitySlider.wholeNumbers = true;
        complexitySlider.minValue = 1; complexitySlider.maxValue = 8; complexitySlider.SetValueWithoutNotify(2);

        showOriginalSlider.wholeNumbers = false;
        showOriginalSlider.minValue = 0; showOriginalSlider.maxValue = 1; showOriginalSlider.SetValueWithoutNotify(0);
        originalImage.color = new Color(1f, 1f, 1f, 0f);

        colorsSlider.onValueChanged.AddListener(OnClusterSettingChanged);
        complexitySlider.onValueChanged.AddListener(OnClusterSettingChanged);
        showOriginalSlider.onValueChanged.AddListener(OnShowOriginalChanged);

        loadButton.onClick.AddListener(OnLoadClicked);
        pathField.onEndEdit.AddListener(LoadFromPath);

        UpdateKLabel();
    }

    void Start() // Start (not Awake) so KMeansClustering.Awake has already run
    {
        if (defaultImage != null) SetSource(defaultImage);
    }

    void OnDestroy()
    {
        if (colorsSlider != null) colorsSlider.onValueChanged.RemoveListener(OnClusterSettingChanged);
        if (complexitySlider != null) complexitySlider.onValueChanged.RemoveListener(OnClusterSettingChanged);
        if (showOriginalSlider != null) showOriginalSlider.onValueChanged.RemoveListener(OnShowOriginalChanged);
        if (loadButton != null) loadButton.onClick.RemoveListener(OnLoadClicked);
        if (pathField != null) pathField.onEndEdit.RemoveListener(LoadFromPath);

        if (resultTexture != null) Destroy(resultTexture);
        if (loadedTexture != null) Destroy(loadedTexture);
    }

    bool ReferencesOk()
    {
        var missing = new List<string>();
        if (clustering == null) missing.Add("Clustering");
        if (resultImage == null) missing.Add("Result Image");
        if (originalImage == null) missing.Add("Original Image");
        if (paletteStrip == null) missing.Add("Palette Strip");
        if (pathField == null) missing.Add("Path Field");
        if (loadButton == null) missing.Add("Load Button");
        if (colorsSlider == null) missing.Add("Colors Slider");
        if (complexitySlider == null) missing.Add("Complexity Slider");
        if (showOriginalSlider == null) missing.Add("Show Original Slider");

        if (missing.Count == 0) return true;
        Debug.LogError("KMeansUI: unassigned Inspector references: " + string.Join(", ", missing), this);
        return false;
    }

    // ---------------- Image loading ----------------
    void OnLoadClicked()
    {
#if UNITY_EDITOR
        string p = UnityEditor.EditorUtility.OpenFilePanel("Select an image", "", "png,jpg,jpeg");
        if (!string.IsNullOrEmpty(p)) { pathField.text = p; LoadFromPath(p); }
#else
        LoadFromPath(pathField.text);
#endif
    }

    void LoadFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path)))
        {
            Debug.LogError("Could not decode image: " + path);
            Destroy(tex);
            return;
        }

        if (loadedTexture != null) Destroy(loadedTexture);
        loadedTexture = tex;
        SetSource(tex);
    }

    void SetSource(Texture tex)
    {
        sourceTexture = tex;
        originalImage.texture = tex;

        float aspect = (float)tex.width / tex.height;
        SetAspect(originalImage, aspect);
        SetAspect(resultImage, aspect);

        if (CanvasMode)
        {
            // K-means doesn't see the full image here: it gets the superpixel texture in RunClustering.
            paintByNumbers.SetImage(tex);
        }
        else
        {
            clustering.SetImage(tex);
        }

        RunClustering();
    }

    static void SetAspect(RawImage img, float aspect)
    {
        var fitter = img.GetComponent<AspectRatioFitter>();
        if (fitter != null) fitter.aspectRatio = aspect;
    }

    // ---------------- Sliders ----------------
    void OnClusterSettingChanged(float _)
    {
        UpdateKLabel();
        if (pending != null) StopCoroutine(pending);
        pending = StartCoroutine(RunAfterDelay());
    }

    System.Collections.IEnumerator RunAfterDelay()
    {
        yield return new WaitForSecondsRealtime(rerunDelay);
        pending = null;
        RunClustering();
    }

    void OnShowOriginalChanged(float t)
    {
        originalImage.color = new Color(1f, 1f, 1f, t);
    }

    void UpdateKLabel()
    {
        if (kLabel != null) kLabel.text = "k=" + (int)colorsSlider.value;
    }

    // ---------------- Clustering + display ----------------
    void RunClustering()
    {
        if (sourceTexture == null) return;

        int k = (int)colorsSlider.value;
        int fragments = (int)complexitySlider.value;

        if (CanvasMode)
        {
            RunCanvasClustering(k, fragments);
            return;
        }

        baseResult = clustering.Run(k);                                                // base palette (k colors)
        fineResult = fragments == 1 ? baseResult : clustering.Run(k * fragments);      // image colors (k x fragments)
        if (baseResult == null || fineResult == null) return;

        clustering.UpdatePalette(baseResult.Centers);
        RefreshPalette(clustering._palette);
        ShowResult();
    }

    // SLIC into K-means: SLIC runs first (only when the segment count or image changed), then K-means
    // clusters ONE PIXEL PER SUPERPIXEL instead of every pixel of the image.
    void RunCanvasClustering(int k, int complexity)
    {
        Texture superPixelTex = paintByNumbers.PrepareSuperPixels(complexity, out bool changed);
        if (superPixelTex == null) return;

        if (changed) clustering.SetImage(superPixelTex);

        baseResult = clustering.Run(k);
        if (baseResult == null) return;

        clustering.UpdatePalette(baseResult.Centers);
        paintByNumbers.ApplyPalette(clustering._palette);   // before the swatches
        RefreshPalette(clustering._palette);
    }

    void ShowResult()
    {
        int w = clustering.Width, h = clustering.Height;
        if (resultTexture == null || resultTexture.width != w || resultTexture.height != h)
        {
            if (resultTexture != null) Destroy(resultTexture);
            resultTexture = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            pixelBuffer = new Color32[w * h];
        }
        resultImage.texture = resultTexture;   // always: the canvas view may have replaced it

        for (int i = 0; i < pixelBuffer.Length; i++)
            pixelBuffer[i] = fineResult.Centers[fineResult.Indices[i]];

        resultTexture.SetPixels32(pixelBuffer);
        resultTexture.Apply(false);
    }

    // Vertical bar: every swatch takes an equal share of the bar's height (darkest at the top).
    void RefreshPalette(List<Color> colors)
    {
        for (int i = paletteStrip.childCount - 1; i >= 0; i--)
            Destroy(paletteStrip.GetChild(i).gameObject);

        for (int i = 0; i < colors.Count; i++)
        {
            var go = new GameObject("Swatch", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(paletteStrip, false);
            go.GetComponent<Image>().color = colors[i];
            var le = go.GetComponent<LayoutElement>();
            le.flexibleWidth = 1;
            le.flexibleHeight = 1;

            // Hovering a swatch highlights that color's regions on the canvas
            if (CanvasMode)
            {
                var swatch = go.AddComponent<PaletteSwatch>();
                swatch.Setup(paintByNumbers, i);
            }
        }
    }
}
