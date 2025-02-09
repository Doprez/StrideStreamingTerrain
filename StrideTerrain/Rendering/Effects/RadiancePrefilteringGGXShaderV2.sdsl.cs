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

namespace StrideTerrain.Rendering.Effects
{
    public static partial class RadiancePrefilteringGGXShaderV2Keys
    {
        public static readonly ValueParameterKey<uint> RadianceMapSize = ParameterKeys.NewValue<uint>();
        public static readonly ValueParameterKey<float> InvRadianceMapSize = ParameterKeys.NewValue<float>();
        public static readonly ObjectParameterKey<Texture> RadianceMap = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> FilteredRadiance = ParameterKeys.NewObject<Texture>();
        public static readonly ValueParameterKey<uint> FilteredRadianceMapSize = ParameterKeys.NewValue<uint>();
        public static readonly ValueParameterKey<float> MipmapCount = ParameterKeys.NewValue<float>();
        public static readonly ValueParameterKey<float> Roughness = ParameterKeys.NewValue<float>();
    }
}
