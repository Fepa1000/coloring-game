using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Minimal test harness for SuperPixelClustering. Plays the algorithm one iteration at a time
/// so you can watch the superpixels settle onto the shapes in the image (like in the video).
/// Right-click the component header -> "Run" to restart it from the Inspector.
/// </summary>
public class SuperPixelDemo : MonoBehaviour
{
    [SerializeField] SuperPixelClustering clustering;
    [SerializeField] Texture2D image;
    [SerializeField] RawImage colorsImage;    // superpixel colors
    [SerializeField] RawImage bordersImage;   // optional: borders only

    [Header("Settings")]
    [SerializeField, Min(1)] int maxSegments = 400;
    [SerializeField, Min(1)] int iterations = 10;
    [SerializeField, Min(0f)] float secondsPerIteration = 0.15f;   // 0 = instant

    void Start() => Run();

    [ContextMenu("Run")]
    public void Run()
    {
        if (clustering == null || image == null || colorsImage == null)
        {
            Debug.LogError("SuperPixelDemo: assign Clustering, Image and Colors Image in the Inspector.", this);
            return;
        }

        StopAllCoroutines();
        StartCoroutine(RunRoutine());
    }

    IEnumerator RunRoutine()
    {
        clustering.SetImage(image);   // recreates the render textures, so assign them afterwards

        colorsImage.texture = clustering.ColorTexture;
        if (bordersImage != null) bordersImage.texture = clustering.BorderTexture;

        float aspect = (float)image.width / image.height;
        SetAspect(colorsImage, aspect);
        SetAspect(bordersImage, aspect);

        clustering.Begin(maxSegments);
        clustering.Render();
        yield return Wait();

        for (int i = 0; i < iterations; i++)
        {
            clustering.Step();
            clustering.Render();
            yield return Wait();
        }

        Debug.Log($"SLIC finished: {clustering.NumSuperPixels} superpixels, {clustering.Iterations} iterations.");
    }

    object Wait() => secondsPerIteration > 0f ? new WaitForSecondsRealtime(secondsPerIteration) : null;

    static void SetAspect(RawImage img, float aspect)
    {
        if (img == null) return;
        var fitter = img.GetComponent<AspectRatioFitter>();
        if (fitter != null) fitter.aspectRatio = aspect;
    }
}
