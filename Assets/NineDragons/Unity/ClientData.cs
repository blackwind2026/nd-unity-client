using System.IO;

namespace NineDragons.Client
{
    /// <summary>Where the original client's Data\*.XP packages live.</summary>
    public static class ClientData
    {
        /// <summary>Default install used on the dev machine; override via <see cref="NdMapViewer"/>.
        /// Points at the UPDATED [Divine] data (item/monster tables + the map .XNS that set NPC
        /// placement, per the chủ) so NPCs sit in the right spot/map. Fallback to Client_linux below.</summary>
        /// <summary>Sandbox copy of [Divine] the chủ made for the Unity rebuild ("cho riêng mày"):
        /// glTF assets are written here (res\Model\9DModel\) and it has no '[]' path chars. Preferred
        /// when present so the client runs off the copy, leaving the live [Divine] untouched.</summary>
        /// <summary>Thư mục CLIENT chạy (chủ 09/09): build ra THẲNG đây (NineDragonsUnity.exe +
        /// NineDragonsUnity_Data), và game cũng đọc Data từ đây → MỘT thư mục chạy duy nhất, không
        /// tách Builds\Win64. Source vẫn ở C:\NineDragonsUiTool\NineDragons_Unity (để dành build lại).</summary>
        public const string UnityClientDir = @"C:\9Dragons Unity";
        public static readonly string UnityRoot = Path.Combine(UnityClientDir, "res");   // data game 9D (Data→res; khác NineDragonsUnity_Data = engine data của Unity)
        public const string DefaultRoot = @"C:\9Dragons[Divine]\Data";
        public const string FallbackRoot = @"C:\9Dragons[Client_linux]\Data";

        static bool HasData(string r) => File.Exists(Path.Combine(r, "Environment", "VR_ENVIRONMENT.XP"));
        public static string Root =
            HasData(UnityRoot) ? UnityRoot : HasData(DefaultRoot) ? DefaultRoot : FallbackRoot;

        public static string Model => Path.Combine(Root, "Model");
        public static string Texture => Path.Combine(Root, "Texture");
        public static string Environment => Path.Combine(Root, "Environment");
        public static string Script => Path.Combine(Root, "Script");

        public static bool Exists => Directory.Exists(Root) &&
                                     File.Exists(Path.Combine(Environment, "VR_ENVIRONMENT.XP"));
    }
}
