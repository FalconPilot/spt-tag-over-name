using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace TagOverName
{
    /// <summary>
    /// Inverse le comportement vanilla : quand un objet a un tag, c'est le nom court de l'objet
    /// qui est masque (au lieu du libelle du tag qui disparait quand il est trop a l'etroit).
    ///
    /// Vanilla (GridItemView.ResizeTag) : largeur dispo = largeur de la case - Caption.renderedWidth - 2.
    /// Si < 40 px, le texte du tag est cache et il ne reste que la barre coloree.
    /// Ici : si l'objet a un tag valide, on vide Caption, donc la largeur dispo est toujours suffisante.
    ///
    /// Tout passe par la reflexion (aucune reference a Assembly-CSharp) : si une classe/un champ est
    /// renomme dans une future version, le mod se desactive proprement et l'indique dans le log.
    /// </summary>
    [BepInPlugin("com.falconpilot.tagovername", "FalconPilot-TagOverName", "1.0.2")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> HideNameWhenTagged;
        internal static BepInEx.Logging.ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            HideNameWhenTagged = Config.Bind(
                "General",
                "Hide item name when tagged",
                true,
                "When an item has a tag, hide its short name so the full tag label is always shown. " +
                "Reopen the inventory (or edit a tag) to see a change.");

            if (!GridViewAccess.Init(out string error))
            {
                Log.LogError("TagOverName disabled: " + error);
                return;
            }

            var harmony = new Harmony("com.falconpilot.tagovername");
            harmony.Patch(
                GridViewAccess.UpdateItemName,
                postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.UpdateItemNamePostfix)));
            harmony.Patch(
                GridViewAccess.UpdateTag,
                postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.UpdateTagPostfix)));

            if (GridViewAccess.LayoutFixAvailable)
            {
                harmony.Patch(
                    GridViewAccess.ResizeTag,
                    postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.ResizeTagPostfix)));
            }
            else
            {
                Log.LogWarning("TagOverName: layout safety net unavailable (members not found); using basic mode only.");
            }

            Log.LogInfo("TagOverName loaded.");
        }
    }

    internal static class Patches
    {
        private static bool _errorLogged;

        // Le nom court n'est ecrit que par GridItemView.UpdateItemName : on le vide juste apres si l'objet est tagge.
        public static void UpdateItemNamePostfix(object __instance)
        {
            try
            {
                if (Plugin.HideNameWhenTagged.Value && GridViewAccess.HasVisibleTag(__instance))
                {
                    GridViewAccess.SetCaptionText(__instance, string.Empty);
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        // GridItemView.UpdateTag lance ResizeTag, qui mesure Caption.renderedWidth apres un Task.Yield().
        // On vide donc aussi le nom ici (avant la reprise de ResizeTag), et on le restaure
        // si le tag vient d'etre retire (ou si l'option est desactivee).
        public static void UpdateTagPostfix(object __instance)
        {
            try
            {
                if (Plugin.HideNameWhenTagged.Value && GridViewAccess.HasVisibleTag(__instance))
                {
                    GridViewAccess.SetCaptionText(__instance, string.Empty);
                }
                else if (GridViewAccess.CaptionIsEmpty(__instance))
                {
                    GridViewAccess.UpdateItemName.Invoke(__instance, null);
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        // GridItemView.ResizeTag (async) peut laisser le libelle du tag masque et la barre etiree sur toute la
        // largeur (mesure de Caption.renderedWidth perimee, exception, etc.). Quand la tache vanilla est
        // terminee, on refait donc nous-memes la mise en page pour les objets tagues : libelle visible,
        // largeur de la barre = clamp(largeur du texte + 12, 40, largeur de la case - 2), comme le jeu.
        public static void ResizeTagPostfix(object __instance, Task __result)
        {
            try
            {
                if (__result == null || !Plugin.HideNameWhenTagged.Value)
                {
                    return;
                }

                // Reprise sur le thread Unity (meme contexte que le Task.Yield() du jeu).
                __result.ContinueWith(
                    _ => ApplyTagLayout(__instance),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        private static void ApplyTagLayout(object view)
        {
            try
            {
                if (Plugin.HideNameWhenTagged.Value && GridViewAccess.HasVisibleTag(view))
                {
                    GridViewAccess.ForceTagLayout(view);
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        private static void LogOnce(Exception e)
        {
            if (_errorLogged)
            {
                return;
            }

            _errorLogged = true;
            Plugin.Log.LogError("TagOverName error (further errors suppressed): " + e);
        }
    }

    /// <summary>Acces par reflexion aux membres de EFT.UI.DragAndDrop.GridItemView (SPT 4.1.x).</summary>
    internal static class GridViewAccess
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static MethodInfo UpdateItemName;
        internal static MethodInfo UpdateTag;
        internal static MethodInfo ResizeTag;
        internal static bool LayoutFixAvailable;

        private static FieldInfo _caption;        // TMP : nom court
        private static FieldInfo _tagName;        // TMP : libelle du tag
        private static FieldInfo _tagColor;       // Image : barre coloree du tag
        private static PropertyInfo _captionText; // TMP_Text.text
        private static PropertyInfo _item;        // ItemView.Item
        private static MethodInfo _getTagComponent; // Item.GetItemComponent<TagComponent>()
        private static FieldInfo _tagComponentName; // TagComponent.Name
        private static MethodInfo _forceMeshUpdate;  // TMP_Text.ForceMeshUpdate(...) (optionnel)
        private static object[] _forceMeshUpdateArgs;
        private static PropertyInfo _preferredWidth; // TMP_Text.preferredWidth
        private static PropertyInfo _gameObject;     // Component.gameObject
        private static MethodInfo _setActive;        // GameObject.SetActive(bool)
        private static PropertyInfo _graphicRect;    // Graphic.rectTransform
        private static PropertyInfo _sizeDelta;      // RectTransform.sizeDelta
        private static PropertyInfo _viewRect;       // ItemView.RectTransform
        private static FieldInfo _vecX, _vecY;       // Vector2.x / .y

        internal static bool Init(out string error)
        {
            error = null;

            Type view = AccessTools.TypeByName("EFT.UI.DragAndDrop.GridItemView");
            Type item = AccessTools.TypeByName("EFT.InventoryLogic.Item");
            Type tag = AccessTools.TypeByName("EFT.InventoryLogic.TagComponent");
            if (view == null || item == null || tag == null)
            {
                error = "GridItemView / Item / TagComponent not found (game version changed?).";
                return false;
            }

            UpdateItemName = view.GetMethod("UpdateItemName", Any, null, Type.EmptyTypes, null);
            UpdateTag = view.GetMethod("UpdateTag", Any, null, Type.EmptyTypes, null);
            _caption = view.GetField("Caption", Any);
            _tagName = view.GetField("TagName", Any);
            _tagColor = view.GetField("_tagColor", Any);
            _item = view.GetProperty("Item", Any);
            _tagComponentName = tag.GetField("Name", Any);
            _captionText = _caption?.FieldType.GetProperty("text", Any);

            // Optionnel : force TextMeshPro a recalculer son rendu apres qu'on a vide le texte, pour que
            // renderedWidth (lu par ResizeTag une image plus tard) vaille bien 0 et pas l'ancienne valeur.
            _forceMeshUpdate = _caption?.FieldType.GetMethods(Any)
                .Where(m => m.Name == "ForceMeshUpdate")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();
            if (_forceMeshUpdate != null)
            {
                // 1er bool (ignoreActiveState) = true, les autres = valeur par defaut / false.
                _forceMeshUpdateArgs = _forceMeshUpdate.GetParameters()
                    .Select((p, i) => p.ParameterType == typeof(bool) ? (object)(i == 0)
                        : p.HasDefaultValue ? p.DefaultValue : null)
                    .ToArray();
            }

            MethodInfo generic = item.GetMethods(Any).FirstOrDefault(m =>
                m.Name == "GetItemComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            _getTagComponent = generic?.MakeGenericMethod(tag);

            ResolveLayoutMembers(view);

            string missing = string.Join(", ", new (string, object)[]
            {
                ("GridItemView.UpdateItemName", UpdateItemName),
                ("GridItemView.UpdateTag", UpdateTag),
                ("GridItemView.Caption", _caption),
                ("GridItemView.TagName", _tagName),
                ("GridItemView._tagColor", _tagColor),
                ("ItemView.Item", _item),
                ("TMP_Text.text", _captionText),
                ("Item.GetItemComponent<T>", _getTagComponent),
                ("TagComponent.Name", _tagComponentName),
            }.Where(x => x.Item2 == null).Select(x => x.Item1));

            if (missing.Length > 0)
            {
                error = "missing members: " + missing;
                return false;
            }

            return true;
        }

        // Filet de securite optionnel : si un membre manque, on continue sans (mode de base).
        private static void ResolveLayoutMembers(Type view)
        {
            try
            {
                ResizeTag = view.GetMethod("ResizeTag", Any, null, Type.EmptyTypes, null);
                _preferredWidth = _tagName?.FieldType.GetProperty("preferredWidth", Any);
                _gameObject = _tagName?.FieldType.GetProperty("gameObject", Any);
                _setActive = _gameObject?.PropertyType.GetMethod("SetActive", Any, null, new[] { typeof(bool) }, null);
                _graphicRect = _tagColor?.FieldType.GetProperty("rectTransform", Any);
                _sizeDelta = _graphicRect?.PropertyType.GetProperty("sizeDelta", Any);
                _viewRect = view.GetProperty("RectTransform", Any);
                Type vec = _sizeDelta?.PropertyType;
                _vecX = vec?.GetField("x", Any | BindingFlags.Public);
                _vecY = vec?.GetField("y", Any | BindingFlags.Public);

                LayoutFixAvailable =
                    ResizeTag != null && typeof(Task).IsAssignableFrom(ResizeTag.ReturnType)
                    && _preferredWidth != null && _setActive != null && _graphicRect != null
                    && _sizeDelta != null && _viewRect != null && _vecX != null && _vecY != null;
            }
            catch (AmbiguousMatchException)
            {
                LayoutFixAvailable = false;
            }
        }

        // Reproduit la branche "assez de place" de GridItemView.ResizeTag.
        internal static void ForceTagLayout(object view)
        {
            object tagName = _tagName.GetValue(view);
            object tagColor = _tagColor.GetValue(view);
            object viewRect = _viewRect.GetValue(view, null);
            if (IsNull(tagName) || IsNull(tagColor) || IsNull(viewRect))
            {
                return;
            }

            float itemWidth = (float)_vecX.GetValue(_sizeDelta.GetValue(viewRect, null));
            float max = itemWidth - 2f;
            if (max < 40f)
            {
                return; // case trop etroite : le jeu n'affiche pas non plus le libelle
            }

            object tagGameObject = _gameObject.GetValue(tagName, null);
            _setActive.Invoke(tagGameObject, new object[] { true });

            float value = (float)_preferredWidth.GetValue(tagName, null) + 12f;
            float width = value < 40f ? 40f : (value > max ? max : value);

            object bar = _graphicRect.GetValue(tagColor, null);
            object size = _sizeDelta.GetValue(bar, null);
            _vecX.SetValue(size, width); // struct boxe : on modifie la copie...
            _sizeDelta.SetValue(bar, size, null); // ...puis on la reaffecte
        }

        // Meme critere que GridItemView.UpdateTag : le tag n'est affiche que si l'UI du tag existe
        // dans ce prefab et que le nom du tag n'est ni vide ni blanc.
        internal static bool HasVisibleTag(object view)
        {
            if (IsNull(_tagColor.GetValue(view)) || IsNull(_tagName.GetValue(view)) || IsNull(_caption.GetValue(view)))
            {
                return false;
            }

            object item = _item.GetValue(view, null);
            if (item == null)
            {
                return false;
            }

            object tag = _getTagComponent.Invoke(item, null);
            if (tag == null)
            {
                return false;
            }

            return _tagComponentName.GetValue(tag) is string name && !string.IsNullOrEmpty(name.Trim());
        }

        internal static bool CaptionIsEmpty(object view)
        {
            object caption = _caption.GetValue(view);
            return !IsNull(caption) && string.IsNullOrEmpty(_captionText.GetValue(caption, null) as string);
        }

        internal static void SetCaptionText(object view, string text)
        {
            object caption = _caption.GetValue(view);
            if (IsNull(caption))
            {
                return;
            }

            _captionText.SetValue(caption, text, null);
            _forceMeshUpdate?.Invoke(caption, _forceMeshUpdateArgs);
        }

        // UnityEngine.Object surcharge Equals : un objet detruit se comporte comme null.
        private static bool IsNull(object o) => o == null || o.Equals(null);
    }
}
