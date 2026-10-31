using UnityEngine;

namespace NineDragons.Client
{
    /// <summary>
    /// Shader lookup that works in BUILDS too: our shaders live under Resources/Shaders so
    /// they are always included (nothing else references them — Shader.Find alone returns
    /// null in a player after build stripping; that was a live crash).
    /// </summary>
    public static class NdShaders
    {
        public static Shader TerrainVC => Load("Shaders/NdTerrainVC", "NineDragons/TerrainVC");
        public static Shader TerrainSplat => Load("Shaders/NdTerrainSplat", "NineDragons/TerrainSplat");
        public static Shader ObjectCutout => Load("Shaders/NdObjectCutout", "NineDragons/ObjectCutout");
        public static Shader Character => Load("Shaders/NdCharacter", "NineDragons/Character");
        public static Shader Water => Load("Shaders/NdWater", "NineDragons/Water");
        public static Shader Grass => Load("Shaders/NdGrass", "NineDragons/Grass");
        public static Shader GrassModel => Load("Shaders/NdGrassModel", "NineDragons/GrassModel");
        public static Shader Foliage => Load("Shaders/NdFoliage", "NineDragons/Foliage");
        public static Shader TreeFoliage => Load("Shaders/NdTreeFoliage", "NineDragons/TreeFoliage");
        public static Shader TreeLeaf => Load("Shaders/NdTreeLeaf", "NineDragons/TreeLeaf");
        public static Shader Particle => Load("Shaders/NdParticle", "NineDragons/Particle");
        public static Shader Glow => Load("Shaders/NdWeaponGlow", "NineDragons/WeaponGlow");   // additive (effect sprites/meshes)
        public static Shader Sharpen => Load("Shaders/NdSharpen", "NineDragons/Sharpen");
        public static Shader PostFx => Load("Shaders/NdPostFx", "NineDragons/PostFx");

        private static Shader Load(string resourcePath, string shaderName)
        {
            var s = Resources.Load<Shader>(resourcePath);
            if (s == null) s = Shader.Find(shaderName);
            if (s == null)
                throw new System.InvalidOperationException($"shader missing from build: {shaderName}");
            return s;
        }
    }
}
