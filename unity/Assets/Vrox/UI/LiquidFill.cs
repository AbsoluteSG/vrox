using UnityEngine;
using UnityEngine.UI;

namespace Vrox.UI
{
    /// <summary>
    /// Feeds <c>Vrox/UI Liquid Fill</c> the three things it cannot work out for
    /// itself: how full the bar is, how big it is, and where its sprite sits in
    /// the atlas.
    /// </summary>
    /// <remarks>
    /// It reads <see cref="Image.fillAmount"/> rather than exposing a fraction of
    /// its own, so whatever already drives the bar — <see cref="PlayerHud"/>, a
    /// boss bar, a tween, a designer dragging the slider in the inspector — keeps
    /// driving it and there is no second copy of "how full is it" to disagree.
    ///
    /// The material is instanced per component. A shared material would mean the
    /// last bar to render sets the fill for every bar sharing it, which shows up
    /// as two health bars locked together and is not obvious from either.
    /// </remarks>
    [ExecuteAlways]
    [RequireComponent(typeof(Image))]
    public sealed class LiquidFill : MonoBehaviour
    {
        private static readonly int FillId = Shader.PropertyToID("_Fill");
        private static readonly int RectSizeId = Shader.PropertyToID("_RectSize");
        private static readonly int SpriteUVId = Shader.PropertyToID("_SpriteUV");

        private Image _image = null!;
        private RectTransform _rect = null!;
        private Material? _source;
        private Material? _instance;

        private void Awake()
        {
            _image = GetComponent<Image>();
            _rect = (RectTransform)transform;
        }

        private void OnEnable()
        {
            var current = _image.material;
            if (current == null || current.shader == null)
            {
                return;
            }

            // Already ours from a previous enable — re-instancing would leak the
            // old one and lose nothing.
            if (_instance != null && current == _instance)
            {
                return;
            }

            if (!current.HasProperty(FillId))
            {
                // Loud rather than silent. Without this the bar renders as a
                // perfectly ordinary flat fill and the missing waves look like a
                // shader that failed to compile somewhere else.
                Debug.LogError(
                    $"[vrox] LiquidFill on '{name}' has material '{current.name}' " +
                    $"(shader '{current.shader.name}'), which has no _Fill property. " +
                    "Assign a material using Vrox/UI Liquid Fill.", this);
                return;
            }

            _source = current;
            _instance = new Material(current) { name = current.name + " (LiquidFill)" };
            _image.material = _instance;
        }

        private void OnDestroy() => Release();
        private void OnDisable() => Release();

        private void Release()
        {
            if (_instance == null)
            {
                return;
            }

            // Put the shared asset back first. Destroying the instance while the
            // Image still points at it leaves the graphic with a null material,
            // which draws untinted white for a frame on the way out.
            if (_image != null && _image.material == _instance)
            {
                _image.material = _source;
            }

            if (Application.isPlaying)
            {
                Destroy(_instance);
            }
            else
            {
                DestroyImmediate(_instance);
            }
            _instance = null;
        }

        /// <summary>
        /// Pushes the current values into the material.
        /// </summary>
        /// <remarks>
        /// Every frame, unconditionally. The sprite and the rect size are cheap
        /// enough that caching them would only add a "did it change" question
        /// that a layout rebuild, an atlas repack or a resolution change can each
        /// answer wrongly.
        /// </remarks>
        private void LateUpdate()
        {
            if (_instance == null)
            {
                return;
            }

            _instance.SetFloat(FillId, Mathf.Clamp01(_image.fillAmount));

            var size = _rect.rect.size;
            _instance.SetVector(RectSizeId, new Vector4(size.x, size.y, 0f, 0f));

            // GetOuterUV returns (xMin, yMin, xMax, yMax) of the sprite within
            // its texture. With no sprite the Image draws the whole rect with
            // 0..1 UVs, which is the same thing.
            var sprite = _image.overrideSprite != null ? _image.overrideSprite : _image.sprite;
            _instance.SetVector(
                SpriteUVId,
                sprite != null
                    ? UnityEngine.Sprites.DataUtility.GetOuterUV(sprite)
                    : new Vector4(0f, 0f, 1f, 1f));
        }
    }
}
