﻿
// Copyright (C) 2016 Luca Piccioni
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
// 
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301
// USA

#undef CULL_CLIPMAP_LEVEL

#define POSITION_CORRECTION

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OpenGL.Scene
{
	/// <summary>
	/// Geometry clipmap implementation.
	/// </summary>
	public class GeometryClipmapObject : SceneGraphObject
	{
		#region Constructors

		/// <summary>
		/// Construct a GeometryClipmapObject.
		/// </summary>
		/// <param name="rank">
		/// A <see cref="UInt16"/> that specify the number of LODs composing this GeometryClipmapObject using a logarithmic scale.
		/// </param>
		/// <param name="levels">
		/// A <see cref="UInt16"/> that specify the number of levels to draw.
		/// </param>
		/// <param name="unit">
		/// A <see cref="Single"/> that specify the size of a single quad unit.
		/// </param>
		public GeometryClipmapObject(ushort rank, ushort levels, float unit)
		{
			if (GraphicsContext.CurrentCaps.GlExtensions.InstancedArrays == false)
				throw new NotSupportedException();
			if (PrimitiveRestart.IsPrimitiveRestartSupported() == false)
				throw new NotSupportedException();

			// Clipmap properties
			StripStride = (uint)Math.Pow(2.0, rank) - 1;
			BlockVertices = (StripStride + 1) / 4;
			ClipmapLevels = levels;
			BlockQuadUnit = unit;

			// Define clipmap resources
			_ClipmapLevels = new ClipmapLevel[levels];
			// Define geometry clipmap program
			_GeometryClipmapProgram = ShadersLibrary.Instance.CreateProgram("GeometryClipmap");
			LinkResource(_GeometryClipmapProgram);
			// Create elevation texture
			uint elevationTextureSize = (uint)((BlockVertices + 1) * 2);

			// Clamp texture size, if necessary
			if (elevationTextureSize > GraphicsContext.CurrentCaps.Limits.MaxTexture2DSize)
				elevationTextureSize = (uint)GraphicsContext.CurrentCaps.Limits.MaxTexture2DSize;

			_ElevationTexture = new TextureArray2d(elevationTextureSize, elevationTextureSize, ClipmapLevels, PixelLayout.GRAYF);
			_ElevationTexture.MinFilter = Texture.Filter.Nearest;
			_ElevationTexture.MagFilter = Texture.Filter.Nearest;
			_ElevationTexture.WrapCoordR = Texture.Wrap.Clamp;
			_ElevationTexture.WrapCoordS = Texture.Wrap.Clamp;
			LinkResource(_ElevationTexture);

			// Define geometry clipmap vertex arrays
			CreateVertexArrays();
		}

		#endregion

		#region Definition

		/// <summary>
		/// The number of vertices composing a line of the clipmap level.
		/// </summary>
		public readonly uint StripStride;

		/// <summary>
		/// The number of vertices compsing a line of the clipmap block.
		/// </summary>
		public readonly uint BlockVertices;

		/// <summary>
		/// Number of levels composing this geometry clipmap.
		/// </summary>
		public readonly ushort ClipmapLevels;

		/// <summary>
		/// The world units that occupy a single quad of a block.
		/// </summary>
		public readonly float BlockQuadUnit;

		#endregion

		#region Geometry Clipmap Levels

		/// <summary>
		/// Geometry clipmap level abstarction.
		/// </summary>
		private class ClipmapLevel : IDisposable
		{
			#region Constructors

			/// <summary>
			/// Construct a ClipmapLevel.
			/// </summary>
			/// <param name="texture">
			/// The <see cref="TextureArray2d"/> holding the elevation data for every clipmap.
			/// </param>
			/// <param name="lod">
			/// A <see cref="UInt32"/> that specify texture array LOD.
			/// </param>
			public ClipmapLevel(TextureArray2d texture, uint lod)
			{
				if (texture == null)
					throw new ArgumentNullException("texture");
				if (texture.Height < lod)
					throw new ArgumentOutOfRangeException("lod", "exceed clipmap levels");

				Texture = texture;
				Texture.IncRef();
				Lod = lod;
			}

			#endregion

			#region Level Information

			/// <summary>
			/// The underlying texture array storing clipmap elevation data.
			/// </summary>
			public readonly TextureArray2d Texture;

			/// <summary>
			/// The Level Of Detail of the texture (i.e. the texture array level).
			/// </summary>
			public readonly uint Lod;

			#endregion

			#region IDisposable Implementation

			/// <summary>
			/// Dispose resources.
			/// </summary>
			public void Dispose()
			{
				Texture.DecRef();
            }

			#endregion
		}

		/// <summary>
		/// Clipmap levels abstraction.
		/// </summary>
		private readonly ClipmapLevel[] _ClipmapLevels;

		#endregion

		#region Geometry Clipmap Blocks

		/// <summary>
		/// Geometry clipmap instanced attribute.
		/// </summary>
		/// <remarks>
		/// Each Clipmap block defines instanced attributes for the geometry clipmap block.
		/// </remarks>
		private struct ClipmapBlockInstance
		{
			#region Constructors

			/// <summary>
			/// Construct a ClipmapBlock.
			/// </summary>
			public ClipmapBlockInstance(uint n, uint m, int x, int y, uint lod, float unit) :
				this(n, m, x, y, lod, unit, new ColorRGBAF(1.0f, 1.0f, 1.0f, 1.0f))
			{

			}

			/// <summary>
			/// Construct a ClipmapBlock.
			/// </summary>
			public ClipmapBlockInstance(uint n, uint m, int x, int y, uint lod, float unit, ColorRGBAF color)
			{
				float scale = (float)Math.Pow(2.0, lod) * unit;
				float positionOffset = -1.0f;

				float xPosition = (x + positionOffset) * scale;
				float yPosition = (y + positionOffset) * scale;

				// Position offset and scale
				Offset = new Vertex4f(xPosition, yPosition, (m - 1) * scale, (m - 1) * scale);
				// Texture coordinate offset and scale
				float texOffset = 0.5f;
				float texScale = (n + 1) / 3601.0f;

				MapOffset = new Vertex4f(texOffset, texOffset, texScale, texScale);
				// LOD
				Lod = lod;
				// Instance color
				BlockColor = color;
			}

			#endregion

			#region Structure

			/// <summary>
			/// Position offset (XY) and scale (ZW).
			/// </summary>
			public Vertex4f Offset;

			/// <summary>
			/// Texture coordinate offset (XY) and scale (ZW).
			/// </summary>
			public Vertex4f MapOffset;

			/// <summary>
			/// The texture level of detail.
			/// </summary>
			public float Lod;

			/// <summary>
			/// Debugging color for instancing.
			/// </summary>
			public ColorRGBAF BlockColor;

			#endregion
		}

		/// <summary>
		/// Blocks array.
		/// </summary>
		private readonly List<ClipmapBlockInstance> _InstancesClipmapBlock = new List<ClipmapBlockInstance>();

		/// <summary>
		/// Ring fix (horizontal).
		/// </summary>
		private readonly List<ClipmapBlockInstance> _InstancesRingFixH = new List<ClipmapBlockInstance>();

		/// <summary>
		/// Ring fix (vertical).
		/// </summary>
		private readonly List<ClipmapBlockInstance> _InstancesRingFixV = new List<ClipmapBlockInstance>();

		/// <summary>
		/// Exterior (horizontal).
		/// </summary>
		private readonly List<ClipmapBlockInstance> _InstancesExteriorH = new List<ClipmapBlockInstance>();

		/// <summary>
		/// Exterior (vertical).
		/// </summary>
		private readonly List<ClipmapBlockInstance> _InstancesExteriorV = new List<ClipmapBlockInstance>();

		#endregion

		#region Elevation Texture Update

		public interface IGeometryClipmapTexSource
		{

		}

		#endregion

		#region Resources

		/// <summary>
		/// Create vertex arrays required for drawing the geometry clipmap blocks.
		/// </summary>
		private void CreateVertexArrays()
		{
			ushort BlockSubdivs = (ushort)(BlockVertices - 1);
			int semiStripStride = ((int)StripStride - 1) / 2;
			uint positionIndex;

			#region Clipmap Blocks

			#region Position

			// Note: this array is shared with ring fixes

			// Create position array buffer ((n+1) x (n+1) vertices equally spaced)
			ArrayBufferObject<Vertex2f> arrayBufferPosition = new ArrayBufferObject<Vertex2f>(BufferObjectHint.StaticCpuDraw);
			float positionStep = 1.0f / BlockSubdivs;

			arrayBufferPosition.Create((uint)(BlockVertices * BlockVertices));
			arrayBufferPosition.Map();

			positionIndex = 0;
			for (float y = 0.0f; y <= 1.0f; y += positionStep) {
				for (float x = 0.0f; x <= 1.0f; x += positionStep, positionIndex++) {
					Debug.Assert(positionIndex < BlockVertices * BlockVertices);
					arrayBufferPosition.Set(new Vertex2f(x, y), positionIndex);
				}
			}

			arrayBufferPosition.Unmap();

			#endregion

			#region Elements

			// Note: this array is shared with ring fixes (horizontal)

			// Create elements indices
			ElementBufferObject<ushort> arrayBufferIndices = new ElementBufferObject<ushort>(BufferObjectHint.StaticCpuDraw);
			List<ushort> arrayBufferElements = new List<ushort>();

			arrayBufferIndices.RestartIndexEnabled = true;

			for (short i = 0; i < BlockSubdivs; i++) {
				ushort baseIndex = (ushort)(i * (BlockSubdivs + 1));

				arrayBufferElements.Add((ushort)(baseIndex));

				for (ushort x = 0; x < BlockSubdivs; x++) {
					arrayBufferElements.Add((ushort)(baseIndex + x + BlockSubdivs + 1));
					arrayBufferElements.Add((ushort)(baseIndex + x + 1));
				}
				arrayBufferElements.Add((ushort)(baseIndex + BlockSubdivs + 1 + BlockSubdivs));

				if (i < BlockSubdivs - 1)
					arrayBufferElements.Add((ushort)arrayBufferIndices.RestartIndexKey);
			}

			arrayBufferIndices.Create(arrayBufferElements.ToArray());

			#endregion

			// Instances
			GenerateLevelBlocks();
			_ArrayClipmapBlockInstances = new ArrayBufferObjectInterleaved<ClipmapBlockInstance>(BufferObjectHint.DynamicCpuDraw);
			_ArrayClipmapBlockInstances.Create((uint)_InstancesClipmapBlock.Count);

			// Create blocks array
			_BlockArray = CreateVertexArrays(arrayBufferPosition, _ArrayClipmapBlockInstances, arrayBufferIndices, 0, 0);
			LinkResource(_BlockArray);

			#endregion

			#region Clipmap Ring Fixes (Horizontal)

			// Instances
			GenerateRingFixInstancesH();
			_ArrayRingFixHInstances = new ArrayBufferObjectInterleaved<ClipmapBlockInstance>(BufferObjectHint.DynamicCpuDraw);
			_ArrayRingFixHInstances.Create((uint)_InstancesRingFixH.Count);

			// Create ring fixes array
			_RingFixArrayH = CreateVertexArrays(arrayBufferPosition, _ArrayRingFixHInstances, arrayBufferIndices, 0, BlockVertices * 4 + 3);
			LinkResource(_RingFixArrayH);

			#endregion

			#region Clipmap Ring Fixes (Vertical)

			#region Elements

			// Create custom elements indices for ring fixes (vertical only)
			ElementBufferObject<ushort> arrayBufferIndicesV = new ElementBufferObject<ushort>(BufferObjectHint.StaticCpuDraw);
			List<ushort> arrayBufferElementsV = new List<ushort>();

			arrayBufferIndicesV.RestartIndexEnabled = true;

			for (ushort i = 0; i < 2; i++) {
				ushort baseIndex = i;

				arrayBufferElementsV.Add((ushort)(baseIndex));

				for (ushort y = 0; y < BlockSubdivs; y++) {
					arrayBufferElementsV.Add((ushort)(baseIndex + (y * BlockVertices) + 1));
					arrayBufferElementsV.Add((ushort)(baseIndex + ((y + 1) * BlockVertices)));
				}
				arrayBufferElementsV.Add((ushort)(baseIndex + (BlockSubdivs * BlockVertices) + 1));

				if (i < 1)
					arrayBufferElementsV.Add((ushort)arrayBufferIndices.RestartIndexKey);
			}

			arrayBufferIndicesV.Create(arrayBufferElementsV.ToArray());

			#endregion

			// Instances
			GenerateRingFixInstancesV();
			_ArrayRingFixVInstances = new ArrayBufferObjectInterleaved<ClipmapBlockInstance>(BufferObjectHint.StaticCpuDraw);
			_ArrayRingFixVInstances.Create((uint)_InstancesRingFixV.Count);

			// Create ring fixes array
			_RingFixArrayV = CreateVertexArrays(arrayBufferPosition, _ArrayRingFixVInstances, arrayBufferIndicesV, 0, 0);
			LinkResource(_RingFixArrayV);

			#endregion

			#region Exterior

			// Note:
			// - Exteriors shall be instanceable
			// - Exteriors are 2 quad width
			// - Larger exterior side vertices  V2 = (n + 4) * 4
			// - Shorter exterior side vertices V1 = (n + 2) * 4
			// - Larger exterior side subdivisions  M2 = (n - 1) + 4 => 2^k + 2
			// - Shorter exterior side subdivisions M1 = (n - 1) + 2 => 2^k
			// (1)
			// - n is the number of vertices of the clipmap
			// - m is the number of vertices of the clipmap block
			// - n is normally set to 2^k - 1
			// - m is normally set to (n + 1) / 4
			
			#region Position

			// Create position array buffer ((n+4) x (n+4) vertices equally spaced)
			ArrayBufferObject<Vertex2f> exteriorPosition = new ArrayBufferObject<Vertex2f>(BufferObjectHint.StaticCpuDraw);
			float exteriorPositionStep;

			exteriorPosition.Create((uint)(((StripStride + 3) * 4) + ((StripStride + 1) * 4)));
			exteriorPosition.Map();

			positionIndex = 0;

			#region Exterior E2

			// E2 strip is 2+2 quad larger than clipmap level
			exteriorPositionStep = 1.0f / (StripStride + 3);

			// E2 - Left side
			for (float y = 0.0f; y < 1.0f; y += positionStep, positionIndex++) {
				Debug.Assert(positionIndex < BlockVertices * BlockVertices);
				exteriorPosition.Set(new Vertex2f(0.0f, y), positionIndex);
			}
			// E2 - Top side
			for (float x = 0.0f; x < 1.0f; x += positionStep, positionIndex++) {
				Debug.Assert(positionIndex < BlockVertices * BlockVertices);
				exteriorPosition.Set(new Vertex2f(x, 1.0f), positionIndex);
			}
			// E2 - Right side
			for (float y = 1.0f; y > 0.0f; y -= positionStep, positionIndex++) {
				Debug.Assert(positionIndex < BlockVertices * BlockVertices);
				exteriorPosition.Set(new Vertex2f(1.0f, y), positionIndex);
			}
			// E2 - Bottom side
			for (float x = 1.0f; x > 0.0f; x -= positionStep, positionIndex++) {
				Debug.Assert(positionIndex < BlockVertices * BlockVertices);
				exteriorPosition.Set(new Vertex2f(x, 0.0f), positionIndex);
			}

			#endregion

			#region Exterior E1

			exteriorPositionStep = 1.0f / (StripStride + 3);

			#endregion

			exteriorPosition.Unmap();

			#endregion

			#endregion

			#region Exteriors (Horizontal)

			GenerateExteriorInstancesH();
			_ArrayExteriorHInstances = new ArrayBufferObjectInterleaved<ClipmapBlockInstance>(BufferObjectHint.DynamicCpuDraw);
			_ArrayExteriorHInstances.Create((uint)_InstancesExteriorH.Count);

			// Create exterior arrays
			// Reuse indices array buffer, but limiting to 2 triangle strips
			_ExteriorArrayH = CreateVertexArrays(arrayBufferPosition, _ArrayExteriorHInstances, arrayBufferIndices, 0, BlockVertices * 4 + 3);
			LinkResource(_ExteriorArrayH);

			#endregion

			#region Exteriors (Vertical)

			ColorRGBAF OuterColor = new ColorRGBAF(0.7f, 0.7f, 0.0f);

			GenerateExteriorInstancesV();
			_ArrayExteriorVInstances = new ArrayBufferObjectInterleaved<ClipmapBlockInstance>(BufferObjectHint.DynamicCpuDraw);
			_ArrayExteriorVInstances.Create((uint)_InstancesExteriorV.Count);

			// Create exterior arrays
			// Reuse indices array buffer, but limiting to 2 triangle strips
			_ExteriorArrayV = CreateVertexArrays(arrayBufferPosition, _ArrayExteriorVInstances, arrayBufferIndicesV, 0, 0);
			LinkResource(_ExteriorArrayV);

			#endregion
		}

		private VertexArrayObject CreateVertexArrays(ArrayBufferObjectBase positions, ArrayBufferObjectBase instances, ElementBufferObject indices, uint offset, uint count)
		{
			VertexArrayObject _RingFixArrayH = new VertexArrayObject();

			// Reuse position array buffer
			_RingFixArrayH.SetArray(positions, VertexArraySemantic.Position);

			_RingFixArrayH.SetInstancedArray(instances, 0, 1, "hal_BlockOffset", null);
			_RingFixArrayH.SetInstancedArray(instances, 1, 1, "hal_MapOffset", null);
			_RingFixArrayH.SetInstancedArray(instances, 2, 1, "hal_Lod", null);
			_RingFixArrayH.SetInstancedArray(instances, 3, 1, "hal_BlockColor", null);

			// Reuse indices array buffer, but limiting to 2 triangle strips
			_RingFixArrayH.SetElementArray(PrimitiveType.TriangleStrip, indices, offset, count);

			return (_RingFixArrayH);
		}

		private void GenerateLevelBlocks()
		{
			ushort BlockSubdivs = (ushort)(BlockVertices - 1);

			int semiStripStride = ((int)StripStride - 1) / 2;
			int xBlock, yBlock;

			for (ushort level = 0; level < ClipmapLevels; level++) {
				// Line 1
				yBlock = -semiStripStride;
				// Line 1 - 2 left
				xBlock = -semiStripStride;
				for (int i = 0; i < 2; i++, xBlock += BlockSubdivs)
					_InstancesClipmapBlock.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit));
				// Line 1 - 2 right
				xBlock = +semiStripStride - BlockSubdivs * 2;
				for (int i = 0; i < 2; i++, xBlock += BlockSubdivs)
					_InstancesClipmapBlock.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit));

				// Line 2
				yBlock += (int)(BlockVertices - 1);
				// Line 2 - 1 left
				xBlock = -semiStripStride;
				_InstancesClipmapBlock.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit));
				// Bottom right
				xBlock = +semiStripStride - BlockSubdivs;
				_InstancesClipmapBlock.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit));

				// Line 3
				yBlock = +semiStripStride - BlockSubdivs * 2;
				// Line 3 - 1 left
				xBlock = -semiStripStride;
				_InstancesClipmapBlock.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit));
				// Line 3 - 1 right
				xBlock = +semiStripStride - BlockSubdivs;
				_InstancesClipmapBlock.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit));

				// Line 4
				yBlock = +semiStripStride - BlockSubdivs;
				// Line 4 - 2 left
				xBlock = -semiStripStride;
				for (int i = 0; i < 2; i++, xBlock += BlockSubdivs)
					_InstancesClipmapBlock.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit));
				// Line 4 - 2 right
				xBlock = +semiStripStride - BlockSubdivs * 2;
				for (int i = 0; i < 2; i++, xBlock += BlockSubdivs)
					_InstancesClipmapBlock.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit));
			}
		}

		private void GenerateRingFixInstancesH()
		{
			ColorRGBAF RingFixColor = new ColorRGBAF(1.0f, 0.0f, 1.0f);
			ushort BlockSubdivs = (ushort)(BlockVertices - 1);

			int semiStripStride = ((int)StripStride - 1) / 2;
			int xBlock, yBlock;

			for (ushort level = 0; level < ClipmapLevels; level++) {
				xBlock = -semiStripStride;
				yBlock = -1;
				_InstancesRingFixH.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit, RingFixColor));
				xBlock = +semiStripStride - BlockSubdivs;
				yBlock = -1;
				_InstancesRingFixH.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit, RingFixColor));
			}
		}

		private void GenerateRingFixInstancesV()
		{
			ColorRGBAF RingFixColor = new ColorRGBAF(1.0f, 0.0f, 1.0f);
			ushort BlockSubdivs = (ushort)(BlockVertices - 1);

			int semiStripStride = ((int)StripStride - 1) / 2;
			int xBlock, yBlock;

			for (ushort level = 0; level < ClipmapLevels; level++) {
				xBlock = -1;
				yBlock = -semiStripStride;
				_InstancesRingFixV.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit, RingFixColor));
				xBlock = -1;
				yBlock = +semiStripStride - BlockSubdivs;
				_InstancesRingFixV.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit, RingFixColor));
			}
		}

		private void GenerateExteriorInstancesH()
		{
			ColorRGBAF ExteriorColor = new ColorRGBAF(1.0f, 1.0f, 0.0f);
			ushort BlockSubdivs = (ushort)(BlockVertices - 1);

			int semiStripStride = ((int)StripStride - 1) / 2;
			int xBlock, yBlock;

			int exteriorInstancesCountH = ((int)StripStride + 1) / BlockSubdivs;

			for (ushort level = 0; level < ClipmapLevels; level++) {
				yBlock = -semiStripStride - 2;
				xBlock = -semiStripStride - 2;
				for (int i = 0; i < exteriorInstancesCountH; i++, xBlock += BlockSubdivs)
					_InstancesExteriorH.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit, ExteriorColor));

				yBlock = -semiStripStride - 2;
				xBlock = +semiStripStride - BlockSubdivs;
				_InstancesExteriorH.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit, ExteriorColor));
			}
		}

		private void GenerateExteriorInstancesV()
		{
			ColorRGBAF ExteriorColor = new ColorRGBAF(1.0f, 1.0f, 0.0f);
			ushort BlockSubdivs = (ushort)(BlockVertices - 1);

			int semiStripStride = ((int)StripStride - 1) / 2;
			int xBlock, yBlock;

			int exteriorInstancesCountV = ((int)StripStride + 1) / BlockSubdivs;

			for (ushort level = 0; level < ClipmapLevels; level++) {
				xBlock = -semiStripStride - 2;
				yBlock = -semiStripStride - 2;
				for (int i = 0; i < exteriorInstancesCountV; i++, yBlock += BlockSubdivs)
					_InstancesExteriorV.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit, ExteriorColor));

				xBlock = -semiStripStride - 2;
				yBlock = +semiStripStride - BlockSubdivs;
				_InstancesExteriorV.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)xBlock, (int)yBlock, level, BlockQuadUnit, ExteriorColor));
			}
		}

		/// <summary>
		/// Generate a <see cref="List{ClipmapBlockInstance}"/> defining instances to cap the clipmap level.
		/// </summary>
		/// <param name="level">
		/// The <see cref="UInt32"/> that specify the level of the cap.
		/// </param>
		/// <returns>
		/// It returns a <see cref="List{ClipmapBlockInstance}"/> defining instances parameters used for capping
		/// the geometry clipmap cap to be draw with <see cref="_BlockArray"/>.
		/// </returns>
		private List<ClipmapBlockInstance> GenerateLevelBlocksCap(uint level)
		{
			List<ClipmapBlockInstance> capBlocks = new List<ClipmapBlockInstance>();

			ushort BlockSubdivs = (ushort)(BlockVertices - 1);

			int semiStripStride = ((int)StripStride - 1) / 2;

			capBlocks.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)0, (int)0, level, BlockQuadUnit));
			capBlocks.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)-BlockSubdivs, (int)0, level, BlockQuadUnit));
			capBlocks.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)0, (int)-BlockSubdivs, level, BlockQuadUnit));
			capBlocks.Add(new ClipmapBlockInstance(StripStride, BlockVertices, (int)-BlockSubdivs, (int)-BlockSubdivs, level, BlockQuadUnit));

			return (capBlocks);
		}

		/// <summary>
		/// Shader program used for drawing geometry clipmap.
		/// </summary>
		private ShaderProgram _GeometryClipmapProgram;

		/// <summary>
		/// Array buffer object defining instances attributes.
		/// </summary>
		private ArrayBufferObjectInterleaved<ClipmapBlockInstance> _ArrayClipmapBlockInstances;

		/// <summary>
		/// Array buffer object defining instances attributes.
		/// </summary>
		private ArrayBufferObjectInterleaved<ClipmapBlockInstance> _ArrayRingFixHInstances, _ArrayRingFixVInstances;

		/// <summary>
		/// Array buffer object defining instances attributes.
		/// </summary>
		private ArrayBufferObjectInterleaved<ClipmapBlockInstance> _ArrayExteriorHInstances, _ArrayExteriorVInstances;

		/// <summary>
		/// Array buffer object defining instances attributes.
		/// </summary>
		private ArrayBufferObjectInterleaved<ClipmapBlockInstance> _ArrayCapExteriorHInstances, _ArrayCapExteriorVInstances;

		/// <summary>
		/// Vertex arrays for drawing 
		/// </summary>
		private VertexArrayObject _BlockArray;

		/// <summary>
		/// Vertex arrays for drawing ring fix (horizontal and vertical patches).
		/// </summary>
		private VertexArrayObject _RingFixArrayH, _RingFixArrayV;

		/// <summary>
		/// Vertex arrays for drawing exteriors (horizontal and vertical patches).
		/// </summary>
		private VertexArrayObject _ExteriorArrayH, _ExteriorArrayV;

		/// <summary>
		/// Vertex arrays for drawing cap exterior.
		/// </summary>
		private VertexArrayObject _CapExteriorArrayH, _CapExteriorArrayV;

		/// <summary>
		/// Elevation texture.
		/// </summary>
		private TextureArray2d _ElevationTexture;

		#endregion

		#region SceneGraphObject Overrides

		/// <summary>
		/// Draw this SceneGraphObject hierarchy.
		/// </summary>
		/// <param name="ctx">
		/// The <see cref="GraphicsContext"/> used for drawing.
		/// </param>
		/// <param name="ctxScene">
		/// The <see cref="SceneGraphContext"/> used for drawing.
		/// </param>
		protected internal override void Draw(GraphicsContext ctx, SceneGraphContext ctxScene)
		{
			if (ctxScene == null)
				throw new ArgumentNullException("ctx");

			Vertex3d currentPosition = (Vertex3d)ctxScene.CurrentView.LocalModel.Position;

			// Compute visible clipmap levels
			const float HeightGain = 2.5f;

			float viewerHeight = currentPosition.Y;
			float clipmap0Size = BlockQuadUnit * (StripStride - 1);

			_CurrentLevel = 0;
			while (clipmap0Size * Math.Pow(2.0, _CurrentLevel) < viewerHeight * HeightGain)
				_CurrentLevel++;

#if POSITION_CORRECTION
			// Update model of the geometry clipmap
			LocalModel.SetIdentity();
			LocalModel.Translate(new Vertex3f(currentPosition.X, 0.0f, currentPosition.Z));
#endif

			// Base implementation
			base.Draw(ctx, ctxScene);
		}

		/// <summary>
		/// Draw this SceneGraphObject instance.
		/// </summary>
		/// <param name="ctx">
		/// The <see cref="GraphicsContext"/> used for drawing.
		/// </param>
		/// <param name="ctxScene">
		/// The <see cref="SceneGraphContext"/> used for drawing.
		/// </param>
		protected override void DrawThis(GraphicsContext ctx, SceneGraphContext ctxScene)
		{
			if (ctxScene == null)
				throw new ArgumentNullException("ctx");

			CheckCurrentContext(ctx);

			ctxScene.GraphicsStateStack.Current.Apply(ctx, _GeometryClipmapProgram);

			_GeometryClipmapProgram.Bind(ctx);
			_GeometryClipmapProgram.ResetTextureUnits();
			_GeometryClipmapProgram.SetUniform(ctx, "hal_ElevationMap", _ElevationTexture);

			// Set grid offsets
			Vertex3d currentPosition = (Vertex3d)ctxScene.CurrentView.LocalModel.Position;
			Vertex2f[] gridOffsets = new Vertex2f[ClipmapLevels];

			for (uint level = 0; level < ClipmapLevels; level++) {
				double positionModule = BlockQuadUnit * Math.Pow(2.0, level);
				Vertex3d gridPositionOffset = currentPosition % positionModule;

				// Avoid contiguos levels overlapping
				while (gridPositionOffset.x > 0)
					gridPositionOffset.x -= positionModule;
				while (gridPositionOffset.z > 0)
					gridPositionOffset.z -= positionModule;

				gridOffsets[level] = new Vertex2f(-gridPositionOffset.X, -gridPositionOffset.Z);
			}
#if POSITION_CORRECTION
			_GeometryClipmapProgram.SetUniform(ctx, "hal_GridOffset", gridOffsets);
#endif

			// Instance culling
			List<ClipmapBlockInstance> instancesClipmapBlock = new List<ClipmapBlockInstance>(_InstancesClipmapBlock);

#if CULL_CLIPMAP_LEVEL
			// Include cap in clipmap blocks
			instancesClipmapBlock.AddRange(GenerateLevelBlocksCap(_CurrentLevel));
#else
			// Include cap in clipmap blocks
			instancesClipmapBlock.AddRange(GenerateLevelBlocksCap(0));
#endif

			// Cull instances
			uint instancesClipmapBlockCount = CullInstances(ctx, instancesClipmapBlock, _ArrayClipmapBlockInstances);
			uint instancesRingFixHCount = CullInstances(ctx, _InstancesRingFixH, _ArrayRingFixHInstances);
			uint instancesRingFixVCount = CullInstances(ctx, _InstancesRingFixV, _ArrayRingFixVInstances);
			uint instancesExteriorHCount = CullInstances(ctx, _InstancesExteriorH, _ArrayExteriorHInstances);
			uint instancesExteriorVCount = CullInstances(ctx, _InstancesExteriorV, _ArrayExteriorVInstances);

			// Draw clipmap blocks using instanced rendering
			// Note: using instanced arrays, to draw the entire geometry clipmap, only N draw call are necessary
			if (instancesClipmapBlockCount > 0)
				_BlockArray.DrawInstanced(ctx, _GeometryClipmapProgram, instancesClipmapBlockCount);
			if (instancesRingFixHCount > 0)
				_RingFixArrayH.DrawInstanced(ctx, _GeometryClipmapProgram, instancesRingFixHCount);
			if (instancesRingFixVCount > 0)
				_RingFixArrayV.DrawInstanced(ctx, _GeometryClipmapProgram, instancesRingFixVCount);
			//if (instancesExteriorHCount > 0)
			//	_ExteriorArrayH.DrawInstanced(ctx, _GeometryClipmapProgram, instancesExteriorHCount);
			//if (instancesExteriorVCount > 0)
			//	_ExteriorArrayV.DrawInstanced(ctx, _GeometryClipmapProgram, instancesExteriorVCount);
		}

		/// <summary>
		/// Filter an array of <see cref="ClipmapBlockInstance"/> and updates the specifies array buffer.
		/// </summary>
		/// <param name="ctx">
		/// The <see cref="GraphicsContext"/> used for updating <paramref name="arrayBuffer"/>.
		/// </param>
		/// <param name="instances">
		/// The <see cref="List{ClipmapBlockInstance}"/> to be filtered.
		/// </param>
		/// <param name="arrayBuffer">
		/// The <see cref="ArrayBufferObjectInterleaved{ClipmapBlockInstance}"/> to be updated.
		/// </param>
		/// <returns>
		/// It returns the number of items of <paramref name="instances"/> filtered.
		/// </returns>
		private uint CullInstances(GraphicsContext ctx, List<ClipmapBlockInstance> instances, ArrayBufferObjectInterleaved<ClipmapBlockInstance> arrayBuffer)
		{
			List<ClipmapBlockInstance> cull = new List<ClipmapBlockInstance>(instances);

#if CULL_CLIPMAP_LEVEL

			// Filter by level
			// Exclude finer levels depending on viewer height
			cull = cull.FindAll(delegate (ClipmapBlockInstance item) {
				return (item.Lod >= _CurrentLevel);
			});

#endif

			// Update instance arrays
			if (cull.Count > 0)
				arrayBuffer.Update(ctx, cull.ToArray());

			return ((uint)cull.Count);
		}

		/// <summary>
		/// Temporary field used by <see cref="Draw"/> and <see cref="DrawThis"/>.
		/// </summary>
		private uint _CurrentLevel;

		#endregion
	}
}