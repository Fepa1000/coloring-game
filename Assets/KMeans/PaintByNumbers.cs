using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Glue between KMeansUI and SuperPixelClustering, plus the paint-by-numbers palette editing.
/// Flow: PrepareSuperPixels (SLIC, gives the tiny one-pixel-per-superpixel texture K-means clusters)
///       -> KMeansUI runs K-means on it -> ApplyPalette (each superpixel takes its nearest palette color, canvas is drawn).
///
/// Palette editing (the "Paint by Numbers" part of the video):
///   - Click a swatch      -> color picker, swap that number's color (live preview on the canvas).
///   - Right-click swatch  -> restore that number's original K-means color.
///   - Clear button        -> every number becomes unpainted: blank canvas with borders, start from scratch.
///   - Restore button      -> back to the generated palette.
///   - Hover a swatch      -> its regions are highlighted (the "numbers" substitute from the video).
/// Disable this component to get the old K-means-only view back.
/// </summary>
public class PaintByNumbers : MonoBehaviour
{
    [SerializeField] SuperPixelClustering superPixels;
    [SerializeField] RawImage target;   // the same Result Image KMeansUI uses

    [Header("Difficulty")]
    [Tooltip("The Complexity slider (1-8) is multiplied by this to get the max number of segments.")]
    [SerializeField, Min(1)] int segmentsPerComplexity = 100;
    [SerializeField, Min(1)] int iterations = 10;

    [Header("Canvas")]
    [SerializeField] bool showColors = true;
    [Tooltip("Color used to light up a hovered region that has not been painted yet.")]
    [SerializeField] Color highlightColor = new Color(1f, 0.85f, 0.25f);

    [Header("Palette editing (all optional)")]
    [SerializeField] Button clearButton;      // "Clear Palette"
    [SerializeField] Button restoreButton;    // "Restore Palette"
    [Tooltip("Leave empty: a simple HSV picker is created at runtime under the root Canvas.")]
    [SerializeField] ColorPickerPopup picker;

    /// <summary>Raised whenever a palette color / painted state changes. Swatches listen to this.</summary>
    public event Action PaletteChanged;

    public static readonly Color UnpaintedSwatchColor = new Color(0.8f, 0.8f, 0.8f, 1f);

    Texture source;
    Texture superPixelTexture;
    bool slicDone;
    int lastSegments = -1;

    Color[] originalPalette;   // what K-means produced
    Color[] currentPalette;    // what the user has right now
    bool[] painted;            // false = blank (cleared) number
    int hovered = -1;

    public int PaletteCount => currentPalette != null ? currentPalette.Length : 0;

    void OnEnable()
    {
        if (clearButton != null) clearButton.onClick.AddListener(ClearPalette);
        if (restoreButton != null) restoreButton.onClick.AddListener(RestorePalette);
    }

    void OnDisable()
    {
        if (clearButton != null) clearButton.onClick.RemoveListener(ClearPalette);
        if (restoreButton != null) restoreButton.onClick.RemoveListener(RestorePalette);
    }

    // ------------------------------------------------------------------
    // Pipeline (unchanged)
    // ------------------------------------------------------------------

    /// <summary>Called by KMeansUI whenever a new image is loaded.</summary>
    public void SetImage(Texture tex)
    {
        source = tex;
        slicDone = false;
        superPixelTexture = null;
        originalPalette = currentPalette = null;
        painted = null;
        hovered = -1;
        superPixels.SetImage(tex);
    }

    /// <summary>
    /// Runs SLIC if needed (new image or new segment count) and returns the one-pixel-per-superpixel texture.
    /// changed = true when that texture is new, so K-means must load it with SetImage() again.
    /// Returns null if no image has been set.
    /// </summary>
    public Texture PrepareSuperPixels(int complexity, out bool changed)
    {
        changed = false;
        if (source == null) return null;

        int segments = Mathf.Max(1, complexity) * segmentsPerComplexity;
        if (!slicDone || segments != lastSegments)
        {
            superPixels.Begin(segments);
            for (int i = 0; i < iterations; i++) superPixels.Step();

            superPixelTexture = superPixels.BuildSuperPixelTexture();
            slicDone = true;
            lastSegments = segments;
            changed = true;
        }

        return superPixelTexture;
    }

    /// <summary>Assigns every superpixel to its nearest palette color and draws the canvas. Resets any user edits.</summary>
    public void ApplyPalette(IReadOnlyList<Color> palette)
    {
        if (!slicDone || palette == null || palette.Count == 0) return;

        int n = palette.Count;
        originalPalette = new Color[n];
        currentPalette = new Color[n];
        painted = new bool[n];
        for (int i = 0; i < n; i++)
        {
            originalPalette[i] = palette[i];
            currentPalette[i] = palette[i];
            painted[i] = true;
        }
        hovered = -1;

        // Assignment (which superpixel belongs to which number) always uses the ORIGINAL colors,
        // so editing a color later never changes the regions.
        superPixels.ApplyPalette(originalPalette);
        superPixels.ShowColors = showColors;
        target.texture = superPixels.CanvasTexture;

        if (picker != null) picker.Hide();
        Redraw();
        PaletteChanged?.Invoke();
    }

    // ------------------------------------------------------------------
    // Palette editing
    // ------------------------------------------------------------------

    public bool IsPainted(int index) => Valid(index) && painted[index];

    /// <summary>Color a swatch should display (gray when the number is unpainted).</summary>
    public Color GetColor(int index)
    {
        if (!Valid(index)) return UnpaintedSwatchColor;
        return painted[index] ? currentPalette[index] : UnpaintedSwatchColor;
    }

    /// <summary>Paints number <paramref name="index"/> with a user-chosen color.</summary>
    public void SetColor(int index, Color color)
    {
        if (!Valid(index)) return;
        color.a = 1f;
        currentPalette[index] = color;
        painted[index] = true;
        Redraw();
        PaletteChanged?.Invoke();
    }

    /// <summary>Right-click on a swatch: back to the color K-means picked for this number.</summary>
    public void ResetColor(int index)
    {
        if (!Valid(index)) return;
        currentPalette[index] = originalPalette[index];
        painted[index] = true;
        Redraw();
        PaletteChanged?.Invoke();
    }

    /// <summary>Start from scratch: every number unpainted, canvas blank except for the borders.</summary>
    public void ClearPalette()
    {
        if (painted == null) return;
        for (int i = 0; i < painted.Length; i++) painted[i] = false;
        Redraw();
        PaletteChanged?.Invoke();
    }

    /// <summary>Back to the generated palette.</summary>
    public void RestorePalette()
    {
        if (originalPalette == null) return;
        for (int i = 0; i < originalPalette.Length; i++)
        {
            currentPalette[i] = originalPalette[i];
            painted[i] = true;
        }
        Redraw();
        PaletteChanged?.Invoke();
    }

    /// <summary>Opens the color picker for one number (called by PaletteSwatch on click).</summary>
    public void OpenPicker(int index)
    {
        if (!Valid(index)) return;

        if (picker == null)
        {
            if (target == null || target.canvas == null)
            {
                Debug.LogError("PaintByNumbers: needs a Canvas to create the color picker.", this);
                return;
            }
            picker = ColorPickerPopup.Create(target.canvas.rootCanvas.transform);
        }

        Color start = painted[index] ? currentPalette[index] : new Color(0.5f, 0.5f, 0.5f, 1f);
        picker.Show(start, c => SetColor(index, c), "Color " + (index + 1));
    }

    // ------------------------------------------------------------------
    // Canvas display
    // ------------------------------------------------------------------

    /// <summary>Hook this to a Toggle's OnValueChanged: true = filled canvas, false = blank canvas.</summary>
    public void SetShowColors(bool value)
    {
        showColors = value;
        Redraw();
    }

    public void Highlight(int paletteIndex)
    {
        hovered = paletteIndex;
        Redraw();
    }

    public void ClearHighlight() => Highlight(-1);

    bool Valid(int index) => currentPalette != null && index >= 0 && index < currentPalette.Length;

    /// <summary>Uploads the palette colors to the GPU and re-renders the canvas. Cheap enough for every hover / drag.</summary>
    void Redraw()
    {
        if (!slicDone || currentPalette == null) return;

        int n = currentPalette.Length;
        var colors = new Color[n];
        for (int i = 0; i < n; i++)
            colors[i] = painted[i] ? currentPalette[i] : Color.white;   // unpainted = white = blank canvas

        // An unpainted number has no color to show, so hovering it lights its regions up in the highlight color.
        if (hovered >= 0 && hovered < n && !painted[hovered])
            colors[hovered] = highlightColor;

        superPixels.SetPaletteColors(colors);
        superPixels.ShowColors = showColors;
        superPixels.Highlight = hovered;
        superPixels.RenderCanvas();
    }
}
