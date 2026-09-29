using System;
using System.Linq;
using System.Reflection;
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
    [BepInPlugin("com.falconpilot.tagovername", "FalconPilot-TagOverName", "1.0.0")]
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

        private static FieldInfo _caption;        // TMP : nom court
        private static FieldInfo _tagName;        // TMP : libelle du tag
        private static FieldInfo _tagColor;       // Image : barre coloree du tag
        private static PropertyInfo _captionText; // TMP_Text.text
        private static PropertyInfo _item;        // ItemView.Item
        private static MethodInfo _getTagComponent; // Item.GetItemComponent<TagComponent>()
        private static FieldInfo _tagComponentName; // TagComponent.Name

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

            MethodInfo generic = item.GetMethods(Any).FirstOrDefault(m =>
                m.Name == "GetItemComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            _getTagComponent = generic?.MakeGenericMethod(tag);

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
            if (!IsNull(caption))
            {
                _captionText.SetValue(caption, text, null);
            }
        }

        // UnityEngine.Object surcharge Equals : un objet detruit se comporte comme null.
        private static bool IsNull(object o) => o == null || o.Equals(null);
    }
}
