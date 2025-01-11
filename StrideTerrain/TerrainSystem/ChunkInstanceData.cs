﻿using Stride.Core.Mathematics;
using System.Runtime.InteropServices;

namespace StrideTerrain.TerrainSystem;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ChunkInstanceData
{
    public byte LodLevel;
    public byte North;
    public byte South;
    public byte West;

    public byte East;
    public byte ChunkX;
    public byte ChunkZ;
    public byte Padding0;

    public float Scale;
    public Vector3 Position;

    public int UvX;
    public int UvY;

    public override readonly string ToString()
        => $"n:{North}, s:{South}, w:{West}, e:{East}";
};