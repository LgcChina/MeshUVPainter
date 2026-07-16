#if UNITY_EDITOR
using UnityEngine;

[System.Serializable]
internal class LgcStampSettings
{
    public Texture2D StampTexture;
    public Vector2 UV = new Vector2(0.5f, 0.5f);
    public float Rotation = 0f;
    public float Scale = 0.25f;
    [Range(0f, 1f)] public float Opacity = 1f;
    public bool ShowPreview = true;
    public bool EnableSymmetry = false; // ¡ï Ä¬ÈÏ¹Ø±Õ
    public bool MirrorPattern = false;

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
            ShowPreview = this.ShowPreview,
            EnableSymmetry = this.EnableSymmetry,
            MirrorPattern = this.MirrorPattern
        };
    }
}
#endif