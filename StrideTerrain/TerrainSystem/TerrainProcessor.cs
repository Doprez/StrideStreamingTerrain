﻿using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Physics;
using Stride.Profiling;
using Stride.Rendering;
using StrideTerrain.TerrainSystem.Effects;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Buffer = Stride.Graphics.Buffer;

namespace StrideTerrain.TerrainSystem;

public class TerrainProcessor : EntityProcessor<TerrainComponent, TerrainRuntimeData>, IEntityComponentRenderProcessor
{
    private readonly Dictionary<RenderModel, TerrainRuntimeData> _modelToTerrainMap = [];

    public VisibilityGroup VisibilityGroup { get; set; } = null!;

    protected override TerrainRuntimeData GenerateComponentData([NotNull] Entity entity, [NotNull] TerrainComponent component)
        => new();

    protected override void OnEntityComponentRemoved(Entity entity, [NotNull] TerrainComponent component, [NotNull] TerrainRuntimeData data)
    {
        base.OnEntityComponentRemoved(entity, component, data);

        if (data.RenderModel != null)
            _modelToTerrainMap.Remove(data.RenderModel);

        foreach (var e in data.PhysicsEntities.Values)
        {
            e.Scene = null;
        }
        data.PhysicsEntities.Clear();

        foreach (var e in data.PhysicsEntityPool)
        {
            e.Scene = null;
        }
        data.PhysicsEntityPool.Clear();

        data.ChunkInstanceDataBuffer?.Dispose();
        data.ChunkInstanceDataBuffer = null;

        data.SectorToChunkInstanceMapBuffer?.Dispose();
        data.SectorToChunkInstanceMapBuffer = null;

        data.TerrainStream?.Dispose();
        data.TerrainStream = null;

        data.HeightmapTexture?.Dispose();
        data.HeightmapTexture = null;

        data.NormalMapTexture?.Dispose();
        data.NormalMapTexture = null;

        data.HeightmapStagingTexture?.Dispose();
        data.HeightmapStagingTexture = null;

        data.NormalMapStagingTexture?.Dispose();
        data.NormalMapStagingTexture = null;

        entity.Remove<ModelComponent>();
    }

    public override void Draw(RenderContext context)
    {
        base.Draw(context);

        var camera = Services.GetService<SceneSystem>().TryGetMainCamera();
        if (camera == null)
            return;

        var graphicsDevice = Services.GetService<IGraphicsDeviceService>().GraphicsDevice;
        var debugTextSystem = Services.GetService<DebugTextSystem>();
        var graphicsContext = Services.GetService<GraphicsContext>();
        var contentManager = Services.GetService<ContentManager>();

        foreach (var pair in ComponentDatas)
        {
            var component = pair.Key;
            var data = pair.Value;

            if (component.Material == null)
                continue;

            if (component.TerrainData == null || component.TerrainStreamingData == null)
                continue;

            if (!component.FreezeCamera)
                data.CameraPosition = camera.GetWorldPosition();

            if (!component.FreezeFrustum)
                data.CameraFrustum = camera.Frustum;

            data.HeightmapTexture ??= Texture.New2D(graphicsDevice, TerrainRuntimeData.RuntimeTextureSize, TerrainRuntimeData.RuntimeTextureSize, PixelFormat.R16_UNorm);
            data.NormalMapTexture ??= Texture.New2D(graphicsDevice, TerrainRuntimeData.RuntimeTextureSize, TerrainRuntimeData.RuntimeTextureSize, PixelFormat.R8G8B8A8_UNorm);

            // Setup model/mesh if needed
            var entity = component.Entity;
            var modelComponent = entity.GetOrCreate<ModelComponent>();

            if (modelComponent.Model == null)
            {
                modelComponent.Model ??= [data.Mesh];
                modelComponent.Model.BoundingSphere = new(Vector3.Zero, 10000);
                modelComponent.Model.BoundingBox = BoundingBox.FromSphere(modelComponent.BoundingSphere);
                modelComponent.IsShadowCaster = false;
                modelComponent.Materials[0] = component.Material;
                modelComponent.Enabled = false; // Stays disabled until everything is ready.
            }

            var modelRenderProcessor = EntityManager.GetProcessor<ModelRenderProcessor>();
            if (modelRenderProcessor == null)
                continue; // Just wait until it's available.

            // Get render model and setup mapping so terrain data can be retrieved in the render feature.
            if (data.RenderModel == null)
            {
                modelRenderProcessor!.RenderModels.TryGetValue(modelComponent, out var renderModel);

                if (renderModel == null) throw new Exception("render model not available");

                _modelToTerrainMap[renderModel] = data;
                data.RenderModel = renderModel;
            }

            // Sync material.
            if (modelComponent.Materials[0] != component.Material)
                modelComponent.Materials[0] = component.Material;

            // Load initial data.
            if (data.TerrainDataUrl != component.TerrainData.Url)
            {
                data.HeightmapStagingTexture?.Dispose();
                data.HeightmapStagingTexture = null;

                data.NormalMapStagingTexture?.Dispose();
                data.NormalMapStagingTexture = null;

                var fileProvider = contentManager.FileProvider;
                if (!fileProvider.ContentIndexMap.TryGetValue(component.TerrainStreamingData.Url, out var objectId))
                {
                    return;
                }

                if (!fileProvider.ObjectDatabase.TryGetObjectLocation(objectId, out var url, out var startPosition, out var end))
                {
                    return;
                }

                data.TerrainDataUrl = component.TerrainData.Url;
                data.TerrainStreamDataUrl = component.TerrainStreamingData.Url;
                data.BaseOffset = startPosition;

                using var terrainDataStream = contentManager.OpenAsStream(data.TerrainDataUrl, StreamFlags.None);
                using var terrainDataReader = new BinaryReader(terrainDataStream);
                data.TerrainData.Read(terrainDataReader);

                data.TerrainStream = File.OpenRead(url);

                data.ChunkToTextureIndex = new int[data.TerrainData.Chunks.Length];
                for (var i = 0; i < data.ChunkToTextureIndex.Length; i++)
                {
                    data.ChunkToTextureIndex[i] = -1;
                }

                data.ResidentChunks = new int[data.TerrainData.Chunks.Length];
                data.ResidentChunksCount = 0;

                data.ActiveChunks.Clear();
                data.PendingChunks.Clear();

                foreach (var physicsEntity in data.PhysicsEntities.Values)
                {
                    physicsEntity.Scene = null;
                }
                data.PhysicsEntities.Clear();

                foreach (var physicsEntity in data.PhysicsEntityPool)
                {
                    physicsEntity.Scene = null;
                }
                data.PhysicsEntityPool.Clear();

                // Allocate initial freelist for streaming chunks (entire texture free by default).
                var chunksPerRowStreaming = TerrainRuntimeData.RuntimeTextureSize / data.TerrainData.Header.ChunkTextureSize;
                data.MaxResidentChunks = chunksPerRowStreaming * chunksPerRowStreaming;

                data.HeightmapStagingTexture ??= Texture.New2D(graphicsDevice, data.TerrainData.Header.ChunkTextureSize, data.TerrainData.Header.ChunkTextureSize, PixelFormat.R16_UNorm, usage: GraphicsResourceUsage.Dynamic);
                data.NormalMapStagingTexture ??= Texture.New2D(graphicsDevice, data.TerrainData.Header.ChunkTextureSize, data.TerrainData.Header.ChunkTextureSize, PixelFormat.R8G8B8A8_UNorm, usage: GraphicsResourceUsage.Dynamic);

                // Load permanently resident chunks.
                FullyLoadLod(data.TerrainData.Header.MaxLod);
                FullyLoadLod(data.TerrainData.Header.MaxLod - 1);
            }

            void FullyLoadLod(int lod)
            {
                if (lod < 0) return;
                var chunksPerRow = data.TerrainData.GetNumberOfChunksPerRow(lod);
                var chunksToLoad = chunksPerRow * chunksPerRow;
                for (var i = 0; i < chunksToLoad; i++)
                {
                    var chunkIndex = data.TerrainData.LodChunkOffsets[lod] + i;
                    LoadAndAllocateChunk(chunkIndex);
                }
            }

            bool LoadAndAllocateChunk(int chunkIndex)
            {
                var textureIndex = -1;
                if (data.NextFreeIndex == data.MaxResidentChunks)
                {
                    // Find anything to eject, if possible
                    for (var i = 0; i < data.ResidentChunksCount; i++)
                    {
                        var c = data.ResidentChunks[i];
                        if (!data.ActiveChunks.Contains(c))
                        {
                            textureIndex = data.ChunkToTextureIndex[c];
                            data.ChunkToTextureIndex[c] = -1;
                            data.ResidentChunks[i] = data.ResidentChunks[data.ResidentChunksCount - 1];
                            data.ResidentChunksCount--;

                            if (data.PhysicsEntities.Remove(c, out var physicsEntity))
                            {
                                data.PhysicsEntityPool.Enqueue(physicsEntity);
                            }

                            break;
                        }
                    }

                    if (textureIndex == -1)
                        return false;
                }
                else
                {
                    textureIndex = data.NextFreeIndex++;
                }

                data.ResidentChunks[data.ResidentChunksCount++] = chunkIndex;

                var texturesPerRow = data.HeightmapTexture.Width / data.TerrainData.Header.ChunkTextureSize;
                var tx = (textureIndex % texturesPerRow) * data.TerrainData.Header.ChunkTextureSize;
                var ty = (textureIndex / texturesPerRow) * data.TerrainData.Header.ChunkTextureSize;

                var tempData = ArrayPool<byte>.Shared.Rent(Math.Max(data.TerrainData.Header.HeightmapSize, data.TerrainData.Header.NormalMapSize));

                data.TerrainStream.Seek(data.BaseOffset + data.TerrainData.Chunks[chunkIndex].HeightmapOffset, SeekOrigin.Begin);
                data.TerrainStream.ReadAtLeast(tempData, data.TerrainData.Header.HeightmapSize);

                var targetRegion = new ResourceRegion(tx, ty, 0, data.TerrainData.Header.ChunkTextureSize, data.TerrainData.Header.ChunkTextureSize, 1);

                data.HeightmapStagingTexture.SetData(graphicsContext.CommandList, tempData.AsSpan(0, data.TerrainData.Header.HeightmapSize), 0, 0);
                graphicsContext.CommandList.CopyRegion(data.HeightmapStagingTexture, 0, null, data.HeightmapTexture, 0, tx, ty);

                // Allocate in memory data for hightest lod (used for physics).
                if (chunkIndex >= data.TerrainData.LodChunkOffsets[0])
                {
                    static float ConvertToFloatHeight(float minValue, float maxValue, float value) => MathUtil.InverseLerp(minValue, maxValue, MathUtil.Clamp(value, minValue, maxValue));

                    var chunk = chunkIndex - data.TerrainData.LodChunkOffsets[0];

                    var chunksPerRowLod0 = data.TerrainData.Header.Size / data.TerrainData.Header.ChunkSize;

                    var chunkOffset = data.TerrainData.Header.ChunkSize;

                    var positionX = chunk % chunksPerRowLod0;
                    var positionZ = chunk / chunksPerRowLod0;

                    var chunkWorldPosition = new Vector3(positionX * chunkOffset + chunkOffset * 0.5f, 0, positionZ * chunkOffset + chunkOffset * 0.5f) * component.UnitsPerTexel;

                    var heightmap = ArrayPool<float>.Shared.Rent(data.TerrainData.Header.ChunkTextureSize * data.TerrainData.Header.ChunkTextureSize);
                    for (var y = 0; y < data.TerrainData.Header.ChunkTextureSize; y++)
                    {
                        for (var x = 0; x < data.TerrainData.Header.ChunkTextureSize; x++)
                        {
                            var index = y * data.TerrainData.Header.ChunkTextureSize + x;
                            var height = BitConverter.ToUInt16(tempData, index * 2);
                            heightmap[index] = ConvertToFloatHeight(0, ushort.MaxValue, height) * data.TerrainData.Header.MaxHeight;
                        }
                    }

                    if (!data.PhysicsEntityPool.TryDequeue(out var physicsEntity))
                    {
                        // It would be nice of someone updated stride to not require the use of the obsolote members ...
                        // TODO: decouple physics streaming from rendering and maybe add custom data format for it.
#pragma warning disable CS0618 // Type or member is obsolete
                        var unmanagedArray = new UnmanagedArray<float>(data.TerrainData.Header.ChunkTextureSize * data.TerrainData.Header.ChunkTextureSize);
#pragma warning restore CS0618 // Type or member is obsolete
                        unmanagedArray.Write(heightmap, 0, 0, data.TerrainData.Header.ChunkTextureSize * data.TerrainData.Header.ChunkTextureSize);
                        physicsEntity =
                        [
                            new StaticColliderComponent
                            {
                                ColliderShape = new HeightfieldColliderShape(data.TerrainData.Header.ChunkTextureSize, data.TerrainData.Header.ChunkTextureSize, unmanagedArray, 1.0f, 0.0f, data.TerrainData.Header.MaxHeight, false)
                                {
                                },
                            }
                        ];

                        physicsEntity.Transform.Scale = new Vector3(component.UnitsPerTexel, 1, component.UnitsPerTexel);
                        physicsEntity.Transform.Position = chunkWorldPosition + new Vector3(0, data.TerrainData.Header.MaxHeight * 0.5f, 0);

                        entity.Scene.Entities.Add(physicsEntity);
                    }
                    else
                    {
                        var collider = physicsEntity.Get<StaticColliderComponent>();
                        var heightfield = collider.ColliderShape as HeightfieldColliderShape;
                        using (heightfield!.LockToReadAndWriteHeights())
                        {
                            heightfield.FloatArray.Write(heightmap, 0, 0, data.TerrainData.Header.ChunkTextureSize * data.TerrainData.Header.ChunkTextureSize);
                        }

                        physicsEntity.Transform.Scale = new Vector3(component.UnitsPerTexel, 1, component.UnitsPerTexel);
                        physicsEntity.Transform.Position = chunkWorldPosition + new Vector3(0, data.TerrainData.Header.MaxHeight * 0.5f, 0);

                        collider.UpdatePhysicsTransformation();
                    }

                    ArrayPool<float>.Shared.Return(heightmap);

                    data.PhysicsEntities.Add(chunkIndex, physicsEntity);
                }

                data.TerrainStream.Seek(data.BaseOffset + data.TerrainData.Chunks[chunkIndex].NormalMapOffset, SeekOrigin.Begin);
                data.TerrainStream.ReadAtLeast(tempData, data.TerrainData.Header.NormalMapSize);

                data.NormalMapStagingTexture.SetData(graphicsContext.CommandList, tempData.AsSpan(0, data.TerrainData.Header.NormalMapSize), 0, 0);
                graphicsContext.CommandList.CopyRegion(data.NormalMapStagingTexture, 0, null, data.NormalMapTexture, 0, tx, ty);

                data.ChunkToTextureIndex[chunkIndex] = textureIndex;

                ArrayPool<byte>.Shared.Return(tempData);
                return true;
            }

            void ProcessStreamingRequests()
            {
                var chunksToLoad = 16;
                while (chunksToLoad > 0 & data.PendingChunks.Count > 0)
                {
                    var chunkIndex = data.PendingChunks.Pop();

                    if (!LoadAndAllocateChunk(chunkIndex))
                        break;

                    chunksToLoad--;
                }

                data.ActiveChunks.Clear();
                data.PendingChunks.Clear();
            }

            bool RequestChunk(int chunkIndex)
            {
                data.ActiveChunks.Add(chunkIndex);

                if (data.ChunkToTextureIndex[chunkIndex] == -1)
                {
                    data.PendingChunks.Push(chunkIndex);
                    return false;
                }

                return true;
            }

            ProcessStreamingRequests();

            modelComponent.Enabled = true;

            // Setup chunks, each chunk will be rendered as instance of the base mesh
            // as all chunks have the same amount of vertices, the only difference is in their size (and position).

            var terrainSize = data.TerrainData.Header.Size;
            var unitsPerTexel = component.UnitsPerTexel;
            var chunkSize = data.TerrainData.Header.ChunkSize;
            var lod0Distance = component.Lod0Distance;
            var invTerrainSize = 1.0f / (terrainSize * unitsPerTexel);
            var texturesPerRow = data.HeightmapTexture.Width / data.TerrainData.Header.ChunkTextureSize;

            var worldPosition = component.Entity.Transform.Position;

            var chunksPerRowLod0 = terrainSize / chunkSize;
            var maxChunks = chunksPerRowLod0 * chunksPerRowLod0;

            data.Mesh.Draw.DrawCount = chunkSize * chunkSize * 6;

            if (data.ChunkInstanceData.Length != maxChunks)
            {
                data.ChunkInstanceData = new ChunkInstanceData[maxChunks];
                data.ChunkInstanceDataBuffer?.Dispose();
                data.ChunkInstanceDataBuffer = Buffer.Structured.New(graphicsDevice, maxChunks, Marshal.SizeOf<ChunkInstanceData>(), true);
            }

            if (data.SectorToChunkInstanceMap.Length != maxChunks)
            {
                data.SectorToChunkInstanceMap = new int[maxChunks];
                data.SectorToChunkInstanceMapBuffer?.Dispose();
                data.SectorToChunkInstanceMapBuffer = Buffer.Structured.New(graphicsDevice, maxChunks, sizeof(int), true);
            }

            for (var i = 0; i < data.SectorToChunkInstanceMap.Length; i++)
            {
                data.SectorToChunkInstanceMap[i] = -1;
            }

            data.InstanceCount = 0;

            // Iterate over lod levels
            var maxLod = data.TerrainData.Header.MaxLod; // Max lod = single chunk
            var maxLodSetting = maxLod;
            if (component.MaximumLod >= 0)
                maxLodSetting = Math.Min(component.MaximumLod, maxLodSetting);

            var minLod = Math.Max(0, component.MinimumLod);

            var chunksToProcess = ArrayPool<int>.Shared.Rent(maxChunks);
            var chunksTemp = ArrayPool<int>.Shared.Rent(maxChunks);
            var chunkTempCount = 0;

            var chunkCount = 0;

            var lod = maxLod;
            var scale = 1 << lod;
            var chunksPerRowCurrentLod = terrainSize / (scale * chunkSize);
            var chunksPerRowNextLod = chunksPerRowCurrentLod * 2;
            for (var y = 0; y < chunksPerRowCurrentLod; y++)
            {
                for (var x = 0; x < chunksPerRowCurrentLod; x++)
                {
                    chunksToProcess[chunkCount++] = y * chunksPerRowCurrentLod + x;
                }
            }

            // Process all pending chunks
            while (chunkCount > 0)
            {
                for (var i = 0; i < chunkCount; i++)
                {
                    var chunk = chunksToProcess[i];

                    var positionX = chunk % chunksPerRowCurrentLod;
                    var positionZ = chunk / chunksPerRowCurrentLod;

                    scale = 1 << lod;
                    var chunkOffset = chunkSize * scale;

                    var chunkIndex = data.TerrainData.GetChunkIndex(lod, positionX, positionZ, chunksPerRowCurrentLod);

                    var chunkWorldPosition = new Vector3(positionX * chunkOffset + (chunkOffset * 0.5f), 0, positionZ * chunkOffset + (chunkOffset * 0.5f)) * unitsPerTexel + worldPosition;

                    // Check lod distance
                    var lodDistance = lod0Distance * (1 << lod);

                    var extent = scale * unitsPerTexel * chunkSize * 0.5f;
                    var bounds = new BoundingBoxExt
                    {
                        Center = chunkWorldPosition,
                        Extent = new(extent, 1000, extent) // TODO: This sohuld use max height per chunk ...
                    };

                    var rect = new RectangleF(chunkWorldPosition.X - extent, chunkWorldPosition.Z - extent, extent * 2.0f, extent * 2.0f);
                    var cameraRect = new RectangleF(data.CameraPosition.X - lodDistance, data.CameraPosition.Z - lodDistance, lodDistance * 2.0f, lodDistance * 2.0f);

                    // Split if desired, otherwise add instance for current lod level
                    cameraRect.Intersects(ref rect, out var shouldSplit);
                    shouldSplit &= lod > minLod;
                    if (lod > maxLodSetting) shouldSplit = true;

                    // Request streaming if desired
                    if (shouldSplit)
                    {
                        if (!RequestChunk(data.TerrainData.GetChunkIndex(lod - 1, positionZ * 2 * chunksPerRowNextLod + (positionX * 2)))) shouldSplit = false;
                        if (!RequestChunk(data.TerrainData.GetChunkIndex(lod - 1, positionZ * 2 * chunksPerRowNextLod + (positionX * 2 + 1)))) shouldSplit = false;
                        if (!RequestChunk(data.TerrainData.GetChunkIndex(lod - 1, (positionZ * 2 + 1) * chunksPerRowNextLod + (positionX * 2)))) shouldSplit = false;
                        if (!RequestChunk(data.TerrainData.GetChunkIndex(lod - 1, (positionZ * 2 + 1) * chunksPerRowNextLod + (positionX * 2 + 1)))) shouldSplit = false;
                    }

                    data.ActiveChunks.Add(data.TerrainData.GetChunkIndex(lod, positionX, positionZ, chunksPerRowCurrentLod));

                    // Frustum cull after streaming request have been made to keep them resident.
                    if (component.FrustumCull && !data.CameraFrustum.Contains(ref bounds))
                        continue;

                    if (shouldSplit && lod > minLod)
                    {
                        chunksTemp[chunkTempCount++] = positionZ * 2 * chunksPerRowNextLod + (positionX * 2);
                        chunksTemp[chunkTempCount++] = positionZ * 2 * chunksPerRowNextLod + (positionX * 2 + 1);
                        chunksTemp[chunkTempCount++] = (positionZ * 2 + 1) * chunksPerRowNextLod + (positionX * 2);
                        chunksTemp[chunkTempCount++] = (positionZ * 2 + 1) * chunksPerRowNextLod + (positionX * 2 + 1);
                    }
                    else
                    {
                        var ratioToLod0 = chunksPerRowLod0 / chunksPerRowCurrentLod;
                        var offsetX = ratioToLod0 * positionX;
                        var offsetZ = ratioToLod0 * positionZ;
                        var w = offsetX + ratioToLod0;
                        var h = offsetZ + ratioToLod0;
                        for (var z = offsetZ; z < h; z++)
                        {
                            for (var x = offsetX; x < w; x++)
                            {
                                if (z < 0 || x < 0 || z >= chunksPerRowLod0 || x > chunksPerRowLod0)
                                    continue;

                                var index = z * chunksPerRowLod0 + x;
                                data.SectorToChunkInstanceMap[index] = data.InstanceCount;
                            }
                        }

                        var textureIndex = data.ChunkToTextureIndex[chunkIndex];

                        var tx = (textureIndex % texturesPerRow) * data.TerrainData.Header.ChunkTextureSize;
                        var ty = (textureIndex / texturesPerRow) * data.TerrainData.Header.ChunkTextureSize;

                        data.ChunkInstanceData[data.InstanceCount].UvX = tx;
                        data.ChunkInstanceData[data.InstanceCount].UvY = ty;

                        data.ChunkInstanceData[data.InstanceCount].LodLevel = (byte)lod;
                        data.ChunkInstanceData[data.InstanceCount].ChunkX = (byte)positionX;
                        data.ChunkInstanceData[data.InstanceCount].ChunkZ = (byte)positionZ;
                        data.ChunkInstanceData[data.InstanceCount].Scale = scale * unitsPerTexel;
                        data.ChunkInstanceData[data.InstanceCount].Position = new(positionX * chunkOffset * unitsPerTexel, 0, positionZ * chunkOffset * unitsPerTexel);
                        data.InstanceCount++;
                    }
                }

                // Copy pending chunks for processing
                chunkCount = 0;
                for (var i = 0; i < chunkTempCount; i++)
                {
                    chunksToProcess[i] = chunksTemp[i];
                    chunkCount++;
                }

                chunksPerRowCurrentLod *= 2;
                chunksPerRowNextLod *= 2;
                lod--;

                chunkTempCount = 0;
            }

            ArrayPool<int>.Shared.Return(chunksToProcess);
            ArrayPool<int>.Shared.Return(chunksTemp);

            static byte GetLodDifference(ChunkInstanceData[] chunks, int[] sectorToChunkMap, int x, int z, int chunksPerRow, int ratioToLod0, int lod)
            {
                x = x * ratioToLod0;
                z = z * ratioToLod0;

                if (x < 0 || z < 0 || x >= chunksPerRow || z >= chunksPerRow)
                {
                    return 0;
                }
                else
                {
                    var chunkIndex = sectorToChunkMap[z * chunksPerRow + x];
                    if (chunkIndex == -1)
                        return 0;
                    return (byte)Math.Max(0, chunks[chunkIndex].LodLevel - lod);
                }
            }

            // Calculate lod differences between chunks
            for (var i = 0; i < data.InstanceCount; i++)
            {
                ref var chunk = ref data.ChunkInstanceData[i];
                scale = 1 << chunk.LodLevel;
                var chunksPerRow = terrainSize / (scale * chunkSize);

                var x = chunk.ChunkX;
                var z = chunk.ChunkZ;

                var ratioToLod0 = chunksPerRowLod0 / chunksPerRow;

                chunk.North = GetLodDifference(data.ChunkInstanceData, data.SectorToChunkInstanceMap, x, z - 1, chunksPerRowLod0, ratioToLod0, chunk.LodLevel);
                chunk.South = GetLodDifference(data.ChunkInstanceData, data.SectorToChunkInstanceMap, x, z + 1, chunksPerRowLod0, ratioToLod0, chunk.LodLevel);
                chunk.East = GetLodDifference(data.ChunkInstanceData, data.SectorToChunkInstanceMap, x + 1, z, chunksPerRowLod0, ratioToLod0, chunk.LodLevel);
                chunk.West = GetLodDifference(data.ChunkInstanceData, data.SectorToChunkInstanceMap, x - 1, z, chunksPerRowLod0, ratioToLod0, chunk.LodLevel);
            }

            // Update instancing and material data
            data.ChunkInstanceDataBuffer!.SetData(graphicsContext.CommandList, (ReadOnlySpan<ChunkInstanceData>)data.ChunkInstanceData.AsSpan());
            data.SectorToChunkInstanceMapBuffer!.SetData(graphicsContext.CommandList, (ReadOnlySpan<int>)data.SectorToChunkInstanceMap.AsSpan());

            if (modelComponent.Materials.Count > 0 && modelComponent.Materials[0].Passes.Count > 0)
            {
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.ChunkSize, (uint)chunkSize);
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.InvTerrainTextureSize, TerrainRuntimeData.InvRuntimeTextureSize);
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.InvTerrainSize, invTerrainSize);
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.Heightmap, data.HeightmapTexture);
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.MaxHeight, data.TerrainData.Header.MaxHeight);
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.TerrainWorld, Matrix.Translation(worldPosition));
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.ChunkInstanceDataBuffer, data.ChunkInstanceDataBuffer);
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.SectorToChunkInstanceMapBuffer, data.SectorToChunkInstanceMapBuffer);
                modelComponent.Materials[0].Passes[0].Parameters.Set(TerrainCommonKeys.TerrainNormalMap, data.NormalMapTexture);
            }

            var maxLoadedChunks = (TerrainRuntimeData.RuntimeTextureSize / data.TerrainData.Header.ChunkTextureSize) * TerrainRuntimeData.RuntimeTextureSize / data.TerrainData.Header.ChunkTextureSize;
            debugTextSystem.Print($"Terrain chunk count: {data.InstanceCount}", new(10, 240), new Color4(1, 0, 0, 1));
            debugTextSystem.Print($"Resident chunks: {data.ResidentChunksCount}", new(10, 260), new Color4(1, 0, 0, 1));
            debugTextSystem.Print($"Active chunks: {data.ActiveChunks.Count}", new(10, 280), new Color4(1, 0, 0, 1));
            debugTextSystem.Print($"Pending chunks: {data.PendingChunks.Count}", new(10, 300), new Color4(1, 0, 0, 1));
            debugTextSystem.Print($"Max loaded chunks: {maxLoadedChunks}", new(10, 320), new Color4(1, 0, 0, 1));
            debugTextSystem.Print($"Physics chunks: {data.PhysicsEntities.Count}, Pool: {data.PhysicsEntityPool.Count}", new(10, 340), new Color4(1, 0, 0, 1));
            debugTextSystem.Print($"Camera: {data.CameraPosition.X:0.0f} {data.CameraPosition.Y:0.0f} {data.CameraPosition.Z:0.0f}", new(10, 360), new Color4(1, 0, 0, 1));
        }
    }

    protected override void OnSystemAdd()
    {
        base.OnSystemAdd();

        VisibilityGroup.Tags.Set(TerrainRenderFeature.ModelToTerrainMap, _modelToTerrainMap);
    }

    protected override void OnSystemRemove()
    {
        base.OnSystemRemove();

        VisibilityGroup.Tags.Remove(TerrainRenderFeature.ModelToTerrainMap);
    }
}

