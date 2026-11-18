#if UNITY_EDITOR
// v2.10.0: Shared window symmetry; mirror symmetric stamp patterns by default.
using UnityEngine;

[System.Serializable]
internal class LgcStampSettings
{
    public Texture2D StampTexture;
    public Vector2 UV = new Vector2(0.5f, 0.5f);
    public float Rotation = 0f;
    public float Scale = 0.25f;
    [Range(0f, 1f)] public float Opacity = 1f;
    public Color OverlayColor = Color.white;
    [Range(0f, 1f)] public float ColorStrength = 0f;
    public bool ShowPreview = true;
    public bool MirrorPattern = true;

    public void ResetTransform()
    {
        UV = new Vector2(0.5f, 0.5f);
        Rotation = 0f;
        Scale = 0.25f;
    }

    public LgcStampSettings Clone()
    {
        return new LgcStampSettings
        {
            StampTexture = this.StampTexture,
            UV = this.UV,
            Rotation = this.Rotation,
            Scale = this.Scale,
            Opacity = this.Opacity,
            OverlayColor = this.OverlayColor,
            ColorStrength = this.ColorStrength,
            ShowPreview = this.ShowPreview,
            MirrorPattern = this.MirrorPattern
        };
    }
}
#endif