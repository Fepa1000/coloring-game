using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Small HSV color picker, built entirely in code so it needs no prefab or scene wiring.
/// PaintByNumbers creates it under the root Canvas the first time a swatch is clicked.
/// Unity has no built-in runtime color picker, hence this.
/// </summary>
public class ColorPickerPopup : MonoBehaviour
{
    const int GradientSteps = 64;

    Image preview;
    TMP_Text title;
    Slider hueSlider, satSlider, valSlider;
    Texture2D hueTex, satTex, valTex;

    float h, s, v;
    Action<Color> onChanged;

    /// <summary>Creates the popup (hidden) as a child of <paramref name="canvasRoot"/>.</summary>
    public static ColorPickerPopup Create(Transform canvasRoot)
    {
        var go = new GameObject("ColorPickerPopup", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvasRoot, false);

        var popup = go.AddComponent<ColorPickerPopup>();
        popup.Build();
        go.SetActive(false);
        return popup;
    }

    public void Show(Color start, Action<Color> changed, string heading)
    {
        onChanged = null;   // don't fire while we set the sliders

        Color.RGBToHSV(start, out h, out s, out v);
        hueSlider.SetValueWithoutNotify(h);
        satSlider.SetValueWithoutNotify(s);
        valSlider.SetValueWithoutNotify(v);
        title.text = heading;
        UpdateVisuals();

        onChanged = changed;
        gameObject.SetActive(true);
        transform.SetAsLastSibling();   // draw above everything else
    }

    public void Hide()
    {
        onChanged = null;
        gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (hueTex != null) Destroy(hueTex);
        if (satTex != null) Destroy(satTex);
        if (valTex != null) Destroy(valTex);
    }

    // ------------------------------------------------------------------

    void OnSliderChanged(float _)
    {
        h = hueSlider.value;
        s = satSlider.value;
        v = valSlider.value;
        UpdateVisuals();
        onChanged?.Invoke(Color.HSVToRGB(h, s, v));
    }

    void UpdateVisuals()
    {
        preview.color = Color.HSVToRGB(h, s, v);

        for (int x = 0; x < GradientSteps; x++)
        {
            float t = x / (float)(GradientSteps - 1);
            satTex.SetPixel(x, 0, Color.HSVToRGB(h, t, v));
            valTex.SetPixel(x, 0, Color.HSVToRGB(h, s, t));
        }
        satTex.Apply();
        valTex.Apply();
    }

    // ------------------------------------------------------------------
    // UI construction
    // ------------------------------------------------------------------

    void Build()
    {
        var rt = (RectTransform)transform;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);   // right edge, vertically centered
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-20f, 0f);
        rt.sizeDelta = new Vector2(280f, 250f);

        var bg = GetComponent<Image>();
        bg.color = new Color(0.1f, 0.1f, 0.1f, 0.95f);   // raycast target on: blocks clicks behind the popup

        var layout = gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 12, 12);
        layout.spacing = 10;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        title = MakeText(transform, "Color", 20, TextAlignmentOptions.Left, 26);

        var previewGo = new GameObject("Preview", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        previewGo.transform.SetParent(transform, false);
        previewGo.GetComponent<LayoutElement>().preferredHeight = 40;
        preview = previewGo.GetComponent<Image>();
        preview.raycastTarget = false;

        hueSlider = MakeSlider("Hue", out hueTex);
        satSlider = MakeSlider("Saturation", out satTex);
        valSlider = MakeSlider("Value", out valTex);

        for (int x = 0; x < GradientSteps; x++)
            hueTex.SetPixel(x, 0, Color.HSVToRGB(x / (float)(GradientSteps - 1), 1f, 1f));
        hueTex.Apply();

        MakeCloseButton();
    }

    Slider MakeSlider(string name, out Texture2D gradient)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(LayoutElement), typeof(Slider));
        root.transform.SetParent(transform, false);
        root.GetComponent<LayoutElement>().preferredHeight = 22;

        // Track: a 1-pixel-high gradient texture stretched over the whole slider
        var track = new GameObject("Track", typeof(RectTransform), typeof(RawImage));
        track.transform.SetParent(root.transform, false);
        Stretch((RectTransform)track.transform, 0f);

        gradient = new Texture2D(GradientSteps, 1, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var raw = track.GetComponent<RawImage>();
        raw.texture = gradient;
        raw.raycastTarget = false;

        // Handle
        var area = new GameObject("Handle Slide Area", typeof(RectTransform));
        area.transform.SetParent(root.transform, false);
        var areaRt = (RectTransform)area.transform;
        Stretch(areaRt, 0f);
        areaRt.offsetMin = new Vector2(6f, 0f);
        areaRt.offsetMax = new Vector2(-6f, 0f);

        var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(area.transform, false);
        var handleRt = (RectTransform)handle.transform;
        handleRt.anchorMin = new Vector2(0f, -0.1f);
        handleRt.anchorMax = new Vector2(0f, 1.1f);
        handleRt.sizeDelta = new Vector2(12f, 0f);
        var handleImg = handle.GetComponent<Image>();
        handleImg.color = Color.white;

        var slider = root.GetComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.handleRect = handleRt;
        slider.targetGraphic = handleImg;
        slider.onValueChanged.AddListener(OnSliderChanged);
        return slider;
    }

    void MakeCloseButton()
    {
        var go = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(transform, false);
        go.GetComponent<LayoutElement>().preferredHeight = 30;
        go.GetComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f, 1f);
        go.GetComponent<Button>().onClick.AddListener(Hide);

        var label = MakeText(go.transform, "Close", 18, TextAlignmentOptions.Center, -1);
        Stretch((RectTransform)label.transform, 0f);
    }

    static TMP_Text MakeText(Transform parent, string text, float size, TextAlignmentOptions align, float preferredHeight)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        var t = go.GetComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.raycastTarget = false;

        if (preferredHeight > 0f)
            go.AddComponent<LayoutElement>().preferredHeight = preferredHeight;
        return t;
    }

    static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }
}
