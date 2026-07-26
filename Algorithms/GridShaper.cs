using Assimp;
using g4;
using ILGPU;
using ILGPU.Runtime;
using PropertyTools.DataAnnotations;
using SpaceEditor.Algorithms;
using SpaceEditor.Rocks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace SpaceEditor.Algorithms;

// Unmanaged, SIMD-free representation of a 3D vector for ILGPU
public struct Float3
{
    public float X, Y, Z;
    public Float3(float x, float y, float z) { X = x; Y = y; Z = z; }
}

// Unmanaged representation of a triangle for the GPU
public struct GpuTriangle
{
    public Float3 V0;
    public Float3 V1;
    public Float3 V2;
    public Float3 MinBounds;
    public Float3 MaxBounds;
}

public class GridShaper
{
    public DMesh3 Mesh { get; }
    public DMeshAABBTree3 Tree { get; }

    public GridShaper(DMesh3 mesh, DMeshAABBTree3 tree)
    {
        this.Mesh = mesh;
        this.Tree = tree;
    }

    public static class BlockSizes
    {
        public const string TwoPointFive = "2.5m";
        public const string HalfMeter = "0.5m (VERY VERY slow on large ships!)";
        public const string TwentyFiveC = nameof(TwentyFiveC);
    }

    public class GeneratorSettings
    {
        public bool SlopesUpper { get; set; } = true;
        public bool SlopesLower { get; set; } = true;
        public bool SlopesSides { get; set; } = true;
        public bool SlopesMustBeSupported { get; set; } = false;

        [ItemsSourceProperty(nameof(BlockSizeValues))]
        public string BlockSize { get; set; } = BlockSizes.TwoPointFive;

        [Browsable(false)]
        public List<string> BlockSizeValues { get; } =
        [
            BlockSizes.TwoPointFive,
            BlockSizes.HalfMeter,
            //BlockSizes.TwentyFiveC,
        ];
    }

    public BlueprintMesh Generate(GeneratorSettings settings, CancellationToken ct, IProgress<(double, string)> progress = null)
    {
        var blockSize = settings.BlockSize switch
        {
            BlockSizes.TwoPointFive => ShapeDB.LargeBlockSize,
            BlockSizes.HalfMeter => ShapeDB.MidBlockSize
        };

        var minimalBounds = this.Tree.Bounds;
        minimalBounds.Min -= blockSize;
        minimalBounds.Max += blockSize;

        var boundingBox = new g4.AxisAlignedBox3d(new g4.Vector3d(0), blockSize / 2);
        while (boundingBox.Contains(minimalBounds) == false)
        {
            boundingBox.Scale(2, 2, 2);
        }

        var cellCount = (int)Math.Ceiling(boundingBox.MaxDim / blockSize);
        var indexer = new ShiftGridIndexer3(boundingBox.Min, blockSize);

        // Initialize ILGPU Context
        using var context = Context.CreateDefault();
        using var accelerator = context.GetPreferredDevice(preferCPU: false).CreateAccelerator(context);

        // Prepare Triangle Data
        int triangleCount = this.Mesh.TriangleCount;
        var flatTriangles = new GpuTriangle[triangleCount];
        int tIndex = 0;

        // PHASE 1: Triangles (0% - 10%)
        foreach (var triangle in this.Mesh.EnumerateTriangles())
        {
            ct.ThrowIfCancellationRequested();
            // Report progress periodically to avoid UI thread spam
            if (tIndex % 5000 == 0) progress?.Report((0.1 * ((double)tIndex / triangleCount), "Mesh Flattening..."));

            var box = triangle.ToBox();
            flatTriangles[tIndex++] = new GpuTriangle
            {
                V0 = new Float3((float)triangle.V0.x, (float)triangle.V0.y, (float)triangle.V0.z),
                V1 = new Float3((float)triangle.V1.x, (float)triangle.V1.y, (float)triangle.V1.z),
                V2 = new Float3((float)triangle.V2.x, (float)triangle.V2.y, (float)triangle.V2.z),
                MinBounds = new Float3((float)box.Min.x, (float)box.Min.y, (float)box.Min.z),
                MaxBounds = new Float3((float)box.Max.x, (float)box.Max.y, (float)box.Max.z)
            };
        }

        // Allocate GPU Memory
        using var deviceTriangles = accelerator.Allocate1D(flatTriangles);

        // Flat 1D representation of the 3D voxel grid
        int totalCells = cellCount * cellCount * cellCount;
        int[] initialGrid = new int[totalCells];
        Array.Fill(initialGrid, BlueprintMesh.NoContent);
        using var deviceGrid = accelerator.Allocate1D(initialGrid);

        // Load and compile kernel
        var voxelizeKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<GpuTriangle>, ArrayView<int>, Float3, float, int, int, int>(
            VoxelizationKernel.Voxelize);

        Float3 origin = new Float3((float)boundingBox.Min.x, (float)boundingBox.Min.y, (float)boundingBox.Min.z);

        // Dispatch execution to the GPU
        voxelizeKernel(
            deviceTriangles.IntExtent,
            deviceTriangles.View,
            deviceGrid.View,
            origin,
            blockSize,
            cellCount,
            cellCount,
            cellCount
        );

        // PHASE 2: Voxelization (Jump to 40% after sync)
        accelerator.Synchronize();
        progress?.Report((0.40, "Voxelization..."));
        var flatResults = deviceGrid.GetAsArray1D();

        // Reconstruct the internal BlueprintMesh data structure
        var bmp = new DenseGrid3i(cellCount, cellCount, cellCount, BlueprintMesh.NoContent);

        // PHASE 3: Grid Reconstruction (40% - 70%)
        for (int z = 0; z < cellCount; z++)
        {
            ct.ThrowIfCancellationRequested();
            if (z % 10 == 0) progress?.Report((0.40 + (0.30 * ((double)z / cellCount)), "Grid Reconstruction..."));

            for (int y = 0; y < cellCount; y++)
            {
                for (int x = 0; x < cellCount; x++)
                {
                    int flatIdx = x + (y * cellCount) + (z * cellCount * cellCount);
                    if (flatResults[flatIdx] == 0)
                    {
                        bmp[new g4.Vector3i(x, y, z)] = 0;
                    }
                }
            }
        }

        var blueprint = new BlueprintMesh();
        blueprint.Blocks = bmp;
        blueprint.Coords = indexer;
        blueprint.Shapes = settings.BlockSize switch
        {
            BlockSizes.TwoPointFive => ShapeDB.LargeShapes,
            BlockSizes.HalfMeter => ShapeDB.MidShapes
        };

        // PHASE 4: Slope Generation (70% - 100%)
        progress?.Report((0.70, "Slope Evaluation...")); 
        int totalSlopePasses = (settings.SlopesUpper ? 4 : 0) + (settings.SlopesLower ? 4 : 0) + (settings.SlopesSides ? 4 : 0);
        int executedPasses = 0;

        if (settings.SlopesUpper)
        {
            ExecSlopes(1);
            ExecSlopes(2);
            ExecSlopes(3);
            ExecSlopes(4);
        }

        if (settings.SlopesLower)
        {
            ExecSlopes(5);
            ExecSlopes(6);
            ExecSlopes(7);
            ExecSlopes(8);
        }

        if (settings.SlopesSides)
        {
            ExecSlopes(9);
            ExecSlopes(10);
            ExecSlopes(11);
            ExecSlopes(12);
        }

        void ExecSlopes(int content)
        {
            var shapeInfo = blueprint.Shapes[content];
            var probeDirectionA = -Base6Directions.Vectors[shapeInfo.Forward];
            var probeDirectionB = Base6Directions.Vectors[shapeInfo.Up];
            var supportDirectionA = -probeDirectionA;
            var supportDirectionB = -probeDirectionB;

            foreach (var g in bmp.Indices())
            {
                ct.ThrowIfCancellationRequested();

                if (blueprint[g] != 0) continue;

                if(blueprint[g + probeDirectionA] != BlueprintMesh.NoContent ||blueprint[g + probeDirectionB] != BlueprintMesh.NoContent)
                {
                    continue;
                }

                if (settings.SlopesMustBeSupported)
                {
                    ct.ThrowIfCancellationRequested();
                    if(blueprint[g + supportDirectionA] != 0 || blueprint[g + supportDirectionB] != 0)
                    {
                        continue;
                    }
                }

                bmp[g] = content;
            }
        }

        // Report progress after each directional pass completes
        if (totalSlopePasses > 0)
        {
            executedPasses++;
            progress?.Report((0.70 + (0.30 * ((double)executedPasses / totalSlopePasses)), "Slope Evaluation..."));
        }

        progress?.Report((1.0, "Complete!"));
        return blueprint;
    }

    //Old, CPU based rendering. Kept here as a legacy option, in case using the GPU is not feasible for some reason. It is significantly slower than the GPU version, especially for large meshes.
    public BlueprintMesh GenerateCpu(GeneratorSettings settings, CancellationToken ct, IProgress<(double, string)> progress = null)
    {
        var blockSize = settings.BlockSize switch
        {
            BlockSizes.TwoPointFive => ShapeDB.LargeBlockSize,
            BlockSizes.HalfMeter => ShapeDB.MidBlockSize
        };

        var minimalBounds = this.Tree.Bounds;
        minimalBounds.Min -= blockSize;
        minimalBounds.Max += blockSize;
        
        var boundingBox = new AxisAlignedBox3d(new Vector3d(0), blockSize / 2);
        while (boundingBox.Contains(minimalBounds) == false)
        {
            boundingBox.Scale(2, 2, 2);
        }

        var cellCount = (int) Math.Ceiling(boundingBox.MaxDim / blockSize);
        var indexer = new ShiftGridIndexer3(boundingBox.Min, blockSize);

        var bmp = new DenseGrid3i(cellCount, cellCount, cellCount, BlueprintMesh.NoContent);
        
        var blueprint = new BlueprintMesh();
        blueprint.Blocks = bmp;
        blueprint.Coords = indexer;
        blueprint.Shapes = settings.BlockSize switch
        {
            BlockSizes.TwoPointFive => ShapeDB.LargeShapes,
            BlockSizes.HalfMeter => ShapeDB.MidShapes
        };

        int triangleCount = this.Mesh.TriangleCount;
        int tIndex = 0;

        foreach (var triangle in this.Mesh.EnumerateTriangles())
        {
            ct.ThrowIfCancellationRequested();
            if (tIndex % 1000 == 0) progress?.Report((0.7 * ((double)tIndex / triangleCount), "Triangle Evaluation..."));
            tIndex++;

            var triBox = triangle.ToBox();
            foreach (var cell in Enumerators.BoxRange(triBox, indexer))
            {
                var cellBox = indexer.ToBox(cell);
                if (cellBox.IntersectWithTriangle(triangle) != IntersectResult.NoIntersection)
                {
                    bmp[cell] = 0; //Cube
                }
            }
        }

        progress?.Report((0.70, "Dispatch & Execution..."));
        int totalSlopePasses = (settings.SlopesUpper ? 4 : 0) + (settings.SlopesLower ? 4 : 0) + (settings.SlopesSides ? 4 : 0);
        int executedPasses = 0;

        if (settings.SlopesUpper)
        {
            ExecSlopes(1);
            ExecSlopes(2);
            ExecSlopes(3);
            ExecSlopes(4);
        }

        if (settings.SlopesLower)
        {
            ExecSlopes(5);
            ExecSlopes(6);
            ExecSlopes(7);
            ExecSlopes(8);
        }

        if (settings.SlopesSides)
        {
            ExecSlopes(9);
            ExecSlopes(10);
            ExecSlopes(11);
            ExecSlopes(12);
        }

        void ExecSlopes(int content)
        {
            var shapeInfo = blueprint.Shapes[content];
            var probeDirectionA = -Base6Directions.Vectors[shapeInfo.Forward];
            var probeDirectionB = Base6Directions.Vectors[shapeInfo.Up];
            var supportDirectionA = -probeDirectionA;
            var supportDirectionB = -probeDirectionB;

            foreach (var g in bmp.Indices())
            {
                ct.ThrowIfCancellationRequested();
                if (blueprint[g] != 0)
                    continue;

                if(blueprint[g + probeDirectionA] != BlueprintMesh.NoContent || blueprint[g + probeDirectionB] != BlueprintMesh.NoContent)
                {
                    continue;
                }

                if (settings.SlopesMustBeSupported)
                {
                    if(blueprint[g + supportDirectionA] != 0 || blueprint[g + supportDirectionB] != 0)
                    {
                        continue;
                    }
                }

                bmp[g] = content;
            }

            if (totalSlopePasses > 0)
            {
                executedPasses++;
                progress?.Report((0.70 + (0.30 * ((double)executedPasses / totalSlopePasses)), "Slope Evaluation..."));
            }
        }

        progress?.Report((1.0, "Complete!"));
        return blueprint;
    }
}

    public class GridMesher
    {
        public static DMesh3 Mesh(BlueprintMesh blueprint)
        {
            var grid = blueprint.Blocks;

            var cubes = new Bitmap3(new(grid.ni, grid.nj, grid.nk));
            foreach (var g in grid.Indices())
            {
                cubes[g] = grid[g] == 0;
            }

            var slopeMesh = new DMesh3();
            foreach (var g in grid.Indices())
            {
                if (cubes[g])
                    continue;

                var shapeId = grid[g];
                if (shapeId == BlueprintMesh.NoContent)
                    continue;

                var shapeInfo = blueprint.Shapes[shapeId];

                slopeMesh.AppendMesh
                (
                    shapeInfo.Shape,
                    MathRocks.ForwardUpTranslate
                    (
                        shapeInfo.Forward,
                        shapeInfo.Up,
                        (Vector3f)blueprint.Coords.ToBox(g).Center
                    )
                );
            }

            var cubesSurfaceGenerator = new VoxelSurfaceGenerator();
            cubesSurfaceGenerator.Voxels = cubes;
            cubesSurfaceGenerator.Generate();

            var cubesMesh = cubesSurfaceGenerator.Meshes[0];
            MeshTransforms.Scale(cubesMesh, blueprint.Coords.CellSize);

            // Voxel generator generates around UnitZeroCentered, while indexer rounds down to corner
            var correctionOffset = blueprint.Coords.CellSize / 2;
            MeshTransforms.Translate(cubesMesh, blueprint.Coords.Origin + correctionOffset);


            var finalMesh = cubesMesh;
            finalMesh.AppendMesh(slopeMesh);

            return finalMesh;
        }
    }

    public class ShapeDB
    {
        public const float LargeBlockSize = 2.5f;
        public const float MidBlockSize = 0.5f;
        public const float SmallBlockSize = 0.25f;

        public record ShapeInfo
        {
            public DMesh3 Shape;
            public string Prefab;

            public int Forward = Base6Directions.Forward;
            public int Up = Base6Directions.Up;
        }

        public ShapeInfo[] Shapes { get; }
        public ShapeInfo this[int index] => this.Shapes[index];

        public ShapeDB(params ShapeInfo[] shapes)
        {
            this.Shapes = shapes;
        }

        public static ShapeDB LargeShapes = new
        (
            // Cube
            CubicShape("2eacbbf2-d8fb-4a78-91dc-7b492517ef97", x => x.AppendBox(Dims(LargeBlockSize))),

            // Slopes
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Left, Base6Directions.Up),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Right, Base6Directions.Up),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Forward, Base6Directions.Up),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Backward, Base6Directions.Up),

            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Left, Base6Directions.Down),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Right, Base6Directions.Down),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Forward, Base6Directions.Down),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Backward, Base6Directions.Down),

            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Forward, Base6Directions.Left),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Left, Base6Directions.Backward),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Backward, Base6Directions.Right),
            SlopeShape("f9efcc6c-6c76-4762-bbf0-6013ec969539", LargeBlockSize, Base6Directions.Right, Base6Directions.Forward)
        );

        public static ShapeDB MidShapes = new
        (
            // Cube
            CubicShape("632d7385-12b9-47a6-802a-a610d0cbd1e0", x => x.AppendBox(Dims(MidBlockSize))),

            // Slopes
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Left, Base6Directions.Up),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Right, Base6Directions.Up),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Forward, Base6Directions.Up),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Backward, Base6Directions.Up),

            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Left, Base6Directions.Down),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Right, Base6Directions.Down),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Forward, Base6Directions.Down),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Backward, Base6Directions.Down),

            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Forward, Base6Directions.Left),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Left, Base6Directions.Backward),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Backward, Base6Directions.Right),
            SlopeShape("69902790-3e2d-43d2-81e4-1c0b42bc7461", MidBlockSize, Base6Directions.Right, Base6Directions.Forward)
        );

        private static ShapeInfo SlopeShape(string prefab, float size, int forward, int up)
        {
            var info = CubicShape
            (
                prefab,
                x =>
                {
                    x.AppendSlope
                    (
                        Dims(size),
                        Base6Directions.Vectors[forward],
                        -Base6Directions.Vectors[up]
                    );
                }
            );

            info.Up = up;
            info.Forward = forward;
            return info;
        }

        private static ShapeInfo CubicShape(string prefab, Action<DMesh3> shape)
        {
            return new()
            {
                Prefab = prefab,
                Shape = MakeShape(shape)
            };
        }

        private static AxisAlignedBox3f Dims(float size)
        {
            return new(Vector3f.Zero, size / 2);
        }

        private static DMesh3 MakeShape(Action<DMesh3> factory)
        {
            var mesh = new DMesh3();
            factory(mesh);
            return mesh;
        }
    }

    public class BlueprintMesh
    {
        public const int NoContent = int.MaxValue;

        public DenseGrid3i Blocks;
        public ShiftGridIndexer3 Coords;
        public ShapeDB Shapes;

        public int this[Vector3i index]
        {
            get
            {
                if (this.Blocks.IsValidIndex(index) == false)
                    return NoContent;

                return this.Blocks[index];
            }
        }
    }

    public class BlueprintWriter
    {
        public required string WriteFolder { get; set; }

        public void Write(BlueprintMesh blueprint, string name)
        {
            var sb = new StringBuilder();
            Generate(blueprint, sb);
            File.WriteAllText(Path.Combine(this.WriteFolder, $"{name}.txt"), sb.ToString());
        }

        public void Generate(BlueprintMesh blueprint, StringBuilder sb)
        {
            //Prefab|PositionX|PositionY|PositionZ|ColorHUE|ColorSATURATION|ColorVALUE|OrientationFORWARD|OrientationUP|Integrity
            var blockGrid = blueprint.Blocks;
            foreach (var g in blockGrid.Indices())
            {
                var content = blockGrid[g];
                if (content == BlueprintMesh.NoContent)
                    continue;

                if (content < 0)
                {
                    throw new NotImplementedException("Shape lists will go here");
                }

                var block = blueprint.Shapes[content];
                sb.Append(block.Prefab);
                sb.Append('|');

                var forwardAxis = block.Forward;
                var upAxis = block.Up;

                var cube = blueprint.Coords.ToBox(g);
                var gridPosition = ToInt(cube.Center / ShapeDB.SmallBlockSize);
                gridPosition += PositionOffset
                (
                    //TODO:
                    blueprint.Shapes == ShapeDB.LargeShapes ?
                        new AxisAlignedBox3i(new Vector3i(-4, -4, -4), new Vector3i(5, 5, 5)) :
                        new AxisAlignedBox3i(new Vector3i(0, 0, 0), new Vector3i(1, 1, 1)),
                    forwardAxis,
                    upAxis
                );


                sb.Append(gridPosition.x);
                sb.Append('|');
                sb.Append(gridPosition.y);
                sb.Append('|');
                sb.Append(gridPosition.z);
                sb.Append('|');

                sb.Append(0);
                sb.Append('|');
                sb.Append(0);
                sb.Append('|');
                sb.Append(0.25);
                sb.Append('|');

                sb.Append(forwardAxis);
                sb.Append('|');
                sb.Append(upAxis);
                sb.Append('|');

                sb.Append(1);
                sb.Append('|');

                sb.AppendLine();
            }

            Vector3i PositionOffset(AxisAlignedBox3i blockSize, int blockForward, int blockRight)
            {
                var baseRight = Base6Directions.Vectors[blockRight];
                var baseForward = -Base6Directions.Vectors[blockForward];
                return BlockOffset
                (
                    blockSize,
                    new Matrix3f
                    (
                        (Vector3f)baseRight.Cross(baseForward),
                        (Vector3f)baseRight,
                        (Vector3f)baseForward,
                        bRows: false
                    )
                );
            }

            static Vector3i BlockOffset(AxisAlignedBox3i blockSize, Matrix3f blockOrientation)
            {
                var offsetNegative = (Vector3f)blockSize.Min;
                var offsetPositive = (Vector3f)blockSize.Max;

                var a = ToInt(blockOrientation.Multiply(ref offsetNegative));
                var b = ToInt(blockOrientation.Multiply(ref offsetPositive));

                var minI = new Vector3i(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));
                return blockSize.Min - minI;
            }

            static Vector3i ToInt(Vector3d vec)
            {
                return new
                (
                    (int)Math.Round(vec.x),
                    (int)Math.Round(vec.y),
                    (int)Math.Round(vec.z)
                );
            }
        }
    }


public static class VoxelizationKernel
{
    // GPU-safe math implementations must reside inside this class
    private static int Min(int a, int b) => a < b ? a : b;
    private static int Max(int a, int b) => a > b ? a : b;
    private static float Min(float a, float b) => a < b ? a : b;
    private static float Max(float a, float b) => a > b ? a : b;
    private static float Abs(float v) => v < 0f ? -v : v;
    private static float Min3(float a, float b, float c) => Min(a, Min(b, c));
    private static float Max3(float a, float b, float c) => Max(a, Max(b, c));
    private static int Floor(float val) => val < 0f ? (int)val - 1 : (int)val;
    private static int Ceiling(float val) => val > (int)val ? (int)val + 1 : (int)val;

    public static void Voxelize(
        Index1D index,
        ArrayView<GpuTriangle> triangles,
        ArrayView<int> voxelGrid,
        Float3 gridOrigin,
        float cellSize,
        int gridX,
        int gridY,
        int gridZ)
    {
        var tri = triangles[index];

        int minX = Max(0, Floor((tri.MinBounds.X - gridOrigin.X) / cellSize));
        int minY = Max(0, Floor((tri.MinBounds.Y - gridOrigin.Y) / cellSize));
        int minZ = Max(0, Floor((tri.MinBounds.Z - gridOrigin.Z) / cellSize));

        int maxX = Min(gridX - 1, Ceiling((tri.MaxBounds.X - gridOrigin.X) / cellSize));
        int maxY = Min(gridY - 1, Ceiling((tri.MaxBounds.Y - gridOrigin.Y) / cellSize));
        int maxZ = Min(gridZ - 1, Ceiling((tri.MaxBounds.Z - gridOrigin.Z) / cellSize));

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Float3 cellCenter = new Float3(
                        gridOrigin.X + (x + 0.5f) * cellSize,
                        gridOrigin.Y + (y + 0.5f) * cellSize,
                        gridOrigin.Z + (z + 0.5f) * cellSize
                    );

                    if (CheckTriangleBoxIntersection(tri, cellCenter, cellSize))
                    {
                        int flatIndex = x + (y * gridX) + (z * gridX * gridY);
                        Atomic.Exchange(ref voxelGrid[flatIndex], 0);
                    }
                }
            }
        }
    }

    private static bool CheckTriangleBoxIntersection(GpuTriangle tri, Float3 boxCenter, float cellSize)
    {
        float boxHalf = cellSize * 0.5f;

        // Shift triangle to local AABB coordinate space
        float v0X = tri.V0.X - boxCenter.X; float v0Y = tri.V0.Y - boxCenter.Y; float v0Z = tri.V0.Z - boxCenter.Z;
        float v1X = tri.V1.X - boxCenter.X; float v1Y = tri.V1.Y - boxCenter.Y; float v1Z = tri.V1.Z - boxCenter.Z;
        float v2X = tri.V2.X - boxCenter.X; float v2Y = tri.V2.Y - boxCenter.Y; float v2Z = tri.V2.Z - boxCenter.Z;

        // Compute edge vectors
        float e0X = v1X - v0X; float e0Y = v1Y - v0Y; float e0Z = v1Z - v0Z;
        float e1X = v2X - v1X; float e1Y = v2Y - v1Y; float e1Z = v2Z - v1Z;
        float e2X = v0X - v2X; float e2Y = v0Y - v2Y; float e2Z = v0Z - v2Z;

        // SAT Test 1: Box AABB bounds
        if (Min3(v0X, v1X, v2X) > boxHalf || Max3(v0X, v1X, v2X) < -boxHalf) return false;
        if (Min3(v0Y, v1Y, v2Y) > boxHalf || Max3(v0Y, v1Y, v2Y) < -boxHalf) return false;
        if (Min3(v0Z, v1Z, v2Z) > boxHalf || Max3(v0Z, v1Z, v2Z) < -boxHalf) return false;

        // SAT Test 2: Triangle Plane vs Box Overlap
        float normalX = e0Y * e1Z - e0Z * e1Y;
        float normalY = e0Z * e1X - e0X * e1Z;
        float normalZ = e0X * e1Y - e0Y * e1X;

        float d = -(normalX * v0X + normalY * v0Y + normalZ * v0Z);

        float vminX = normalX > 0f ? -boxHalf : boxHalf; float vmaxX = normalX > 0f ? boxHalf : -boxHalf;
        float vminY = normalY > 0f ? -boxHalf : boxHalf; float vmaxY = normalY > 0f ? boxHalf : -boxHalf;
        float vminZ = normalZ > 0f ? -boxHalf : boxHalf; float vmaxZ = normalZ > 0f ? boxHalf : -boxHalf;

        if ((normalX * vminX + normalY * vminY + normalZ * vminZ) + d > 0f) return false;
        if ((normalX * vmaxX + normalY * vmaxY + normalZ * vmaxZ) + d < 0f) return false;

        // SAT Test 3: Edge Cross Products
        if (!AxisTest(e0Z, -e0Y, v0Y, v0Z, v2Y, v2Z, boxHalf)) return false;
        if (!AxisTest(e1Z, -e1Y, v1Y, v1Z, v0Y, v0Z, boxHalf)) return false;
        if (!AxisTest(e2Z, -e2Y, v2Y, v2Z, v1Y, v1Z, boxHalf)) return false;

        if (!AxisTest(-e0Z, e0X, v0X, v0Z, v2X, v2Z, boxHalf)) return false;
        if (!AxisTest(-e1Z, e1X, v1X, v1Z, v0X, v0Z, boxHalf)) return false;
        if (!AxisTest(-e2Z, e2X, v2X, v2Z, v1X, v1Z, boxHalf)) return false;

        if (!AxisTest(e0Y, -e0X, v0X, v0Y, v2X, v2Y, boxHalf)) return false;
        if (!AxisTest(e1Y, -e1X, v1X, v1Y, v0X, v0Y, boxHalf)) return false;
        if (!AxisTest(e2Y, -e2X, v2X, v2Y, v1X, v1Y, boxHalf)) return false;

        return true;
    }

    private static bool AxisTest(float a, float b, float fa, float fb, float va, float vb, float boxHalf)
    {
        float p0 = a * fa + b * fb;
        float p2 = a * va + b * vb;
        float min = Min(p0, p2);
        float max = Max(p0, p2);
        float rad = (Abs(a) + Abs(b)) * boxHalf;
        return !(min > rad || max < -rad);
    }
}