﻿// <auto-generated>
// Do not edit this file yourself!
//
// This code was generated by Stride Shader Mixin Code Generator.
// To generate it yourself, please install Stride.VisualStudio.Package .vsix
// and re-save the associated .sdfx.
// </auto-generated>

using System;
using Stride.Core;
using Stride.Rendering;
using Stride.Graphics;
using Stride.Shaders;
using Stride.Core.Mathematics;
using Buffer = Stride.Graphics.Buffer;

namespace StrideTerrain.TerrainSystem.Effects
{
    public static partial class TerrainDataKeys
    {
        public static readonly ObjectParameterKey<Texture> Heightmap = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> TerrainNormalMap = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> TerrainControlMap = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Buffer> ChunkBuffer = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> SectorToChunkMapBuffer = ParameterKeys.NewObject<Buffer>();
        public static readonly ValueParameterKey<uint> ChunkSize = ParameterKeys.NewValue<uint>();
        public static readonly ValueParameterKey<float> InvTerrainTextureSize = ParameterKeys.NewValue<float>();
        public static readonly ValueParameterKey<float> TerrainTextureSize = ParameterKeys.NewValue<float>();
        public static readonly ValueParameterKey<float> InvTerrainSize = ParameterKeys.NewValue<float>();
        public static readonly ValueParameterKey<float> MaxHeight = ParameterKeys.NewValue<float>();
        public static readonly ValueParameterKey<uint> ChunksPerRow = ParameterKeys.NewValue<uint>();
        public static readonly ValueParameterKey<float> InvUnitsPerTexel = ParameterKeys.NewValue<float>();
    }
}
