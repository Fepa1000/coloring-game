using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Added to each palette swatch at runtime.
///   Hover        -> highlights that number's regions on the canvas
///   Left click   -> opens the color picker for that number
///   Right click  -> restores that number's original color
/// Shows the number (1-based) on the swatch and follows the palette state (gray when cleared).
/// </summary>
public class PaletteSwatch : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    public PaintByNumbers Owner;
    public int Index;

    Image image;
    TMP_Text label;

    public void Setup(PaintByNumbers owner, int index)
    {
        Owner = owner;
        Index = index;
        image = GetComponent<Image>();
        if (label == null) label = CreateLabel();
        label.text = (index + 1).ToString();

        Owner.PaletteChanged += Refresh;
        Refresh();
    }

    void OnDestroy()
    {
        if (Owner != null) Owner.PaletteChanged -= Refresh;
    }

    void Refresh()
    {
        // Swatches from a previous palette can still be alive for a frame (Destroy is deferred).
        if (image == null || label == null || Owner == null || Index >= Owner.PaletteCount) return;

        Color c = Owner.GetColor(Index);
        image.color = c;

        float luminance = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        label.color = luminance > 0.5f ? Color.black : Color.white;
    }

    TMP_Text CreateLabel()
    {
        var go = new GameObject("Number", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(transform, false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var t = go.GetComponent<TextMeshProUGUI>();
        t.alignment = TextAlignmentOptions.Center;
        t.enableAutoSizing = true;
        t.fontSizeMin = 8;
        t.fontSizeMax = 28;
        t.raycastTarget = false;   // keep hover / click on the swatch itself
        return t;
    }

    public void OnPointerEnter(PointerEventData eventData) => Owner.Highlight(Index);
    public void OnPointerExit(PointerEventData eventData) => Owner.ClearHighlight();

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button == PointerEventData.InputButton.Left) Owner.OpenPicker(Index);
        else if (eventData.button == PointerEventData.InputButton.Right) Owner.ResetColor(Index);
    }
}
