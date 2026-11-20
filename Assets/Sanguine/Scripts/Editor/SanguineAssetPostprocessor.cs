using System.IO;
using UnityEditor;
using UnityEngine;

namespace Sanguine.EditorTools
{
    /// <summary>
    /// Import rules for Sanguine City's generated art (Tools/assets/build_assets.py) and for
    /// drop-in characters:
    ///  - Resources/Textures: *_normal = normal map, *_mask = linear data, anisotropic filtering.
    ///  - Resources/Sky: HDR equirectangular skies without mipmaps (no seam at the wrap).
    ///  - Resources/Characters: rigged models imported as Humanoid avatars.
    ///  - Resources/Animations: Humanoid clips; idle/walk/run style clips loop, in place.
    /// </summary>
    public sealed class SanguineAssetPostprocessor : AssetPostprocessor
    {
        private const string Root = "Assets/Sanguine/Resources/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Root)) return;
            var ti = (TextureImporter)assetImporter;
            string file = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();

            if (assetPath.StartsWith(Root + "Sky/"))
            {
                ti.textureType = TextureImporterType.Default;
                ti.textureShape = TextureImporterShape.Texture2D;
                ti.mipmapEnabled = false;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
                ti.maxTextureSize = 4096;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                return;
            }

            if (assetPath.StartsWith(Root + "Textures/"))
            {
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Repeat;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 2048;
                if (file.EndsWith("_normal"))
                {
                    ti.textureType = TextureImporterType.NormalMap;
                }
                else if (file.EndsWith("_mask"))
                {
                    ti.textureType = TextureImporterType.Default;
                    ti.sRGBTexture = false;
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                }
                else
                {
                    ti.textureType = TextureImporterType.Default;
                    ti.sRGBTexture = true;
                }
            }
        }

        private void OnPreprocessModel()
        {
            var mi = (ModelImporter)assetImporter;
            if (assetPath.StartsWith(Root + "Characters/"))
            {
                mi.animationType = ModelImporterAnimationType.Human;
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                mi.importAnimation = false;
                mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
                mi.isReadable = false;
            }
            else if (assetPath.StartsWith(Root + "Animations/"))
            {
                mi.animationType = ModelImporterAnimationType.Human;
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                mi.importAnimation = true;
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
            }
        }

        private void OnPreprocessAnimation()
        {
            if (!assetPath.StartsWith(Root + "Animations/")) return;
            var mi = (ModelImporter)assetImporter;
            string file = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
            bool once = file.Contains("death") || file.Contains("dying") || file.Contains("die") ||
                        file.Contains("punch") || file.Contains("attack") || file.Contains("strike") || file.Contains("cast");
            ModelImporterClipAnimation[] clips = mi.defaultClipAnimations;
            foreach (ModelImporterClipAnimation c in clips)
            {
                c.loopTime = !once;
                c.lockRootRotation = true;
                c.lockRootHeightY = true;
                c.heightFromFeet = true;
                c.keepOriginalOrientation = true;
            }
            mi.clipAnimations = clips;
        }
    }
}
