using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using float3 = Unity.Mathematics.float3;
using float4x4 = Unity.Mathematics.float4x4;

namespace Deform
{
    [Deformer(Name = "Lattice", Description = "Free-form deform a mesh using lattice control points", Type = typeof(LatticeDeformer))]
    [HelpURL("https://github.com/keenanwoodall/Deform/wiki/LatticeDeformer")]
    public class LatticeDeformer : Deformer
    {
        public Transform Target
        {
            get
            {
                if (target == null)
                    target = transform;
                return target;
            }
            set { target = value; }
        }


        public bool CanAutoFitBounds => transform.GetComponentInParent<Deformable>() != null;

        public float3[] ControlPoints => controlPoints;

        public Vector3Int Resolution => resolution;

        [SerializeField, HideInInspector] private Transform target;
        [SerializeField] private float3[] controlPoints;
        [SerializeField] private Vector3Int resolution = new Vector3Int(2, 2, 2);

        protected virtual void Reset()
        {
            GenerateControlPoints(resolution);

            // Fit to parent deformable by default
            FitBoundsToParentDeformable();
        }

        public void FitBoundsToParentDeformable()
        {
            Deformable deformable = transform.GetComponentInParent<Deformable>();
            if (deformable != null)
            {
                var bounds = deformable.GetCurrentMesh().bounds;
                transform.localPosition = bounds.center;
                transform.localScale = bounds.size;
            }
        }

        public void GenerateControlPoints(Vector3Int newResolution, bool resampleExistingPoints = false)
        {
            if (resampleExistingPoints)
            {
                NativeArray<float3> newControlPoints = new NativeArray<float3>(newResolution.x * newResolution.y * newResolution.z, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                for (int z = 0; z < newResolution.z; z++)
                {
                    for (int y = 0; y < newResolution.y; y++)
                    {
                        for (int x = 0; x < newResolution.x; x++)
                        {
                            int index = GetIndex(newResolution, x, y, z);

                            newControlPoints[index] = new float3(x / (float) (newResolution.x - 1) - 0.5f,
                                y / (float) (newResolution.y - 1) - 0.5f, z / (float) (newResolution.z - 1) - 0.5f);
                        }
                    }
                }

                var latticeJob = new LatticeJob
                {
                    controlPoints = new NativeArray<float3>(controlPoints, Allocator.TempJob),
                    resolution = new int3(resolution.x, resolution.y, resolution.z),
                    meshToTarget = float4x4.identity,
                    targetToMesh = float4x4.identity,
                    vertices = newControlPoints
                };
                latticeJob.Run(newControlPoints.Length);
                resolution = newResolution;

                controlPoints = new float3[newControlPoints.Length];
                newControlPoints.CopyTo(controlPoints);
                newControlPoints.Dispose();
            }
            else
            {
                resolution = newResolution;

                controlPoints = new float3[resolution.x * resolution.y * resolution.z];
                for (int z = 0; z < resolution.z; z++)
                {
                    for (int y = 0; y < resolution.y; y++)
                    {
                        for (int x = 0; x < resolution.x; x++)
                        {
                            int index = GetIndex(x, y, z);

                            controlPoints[index] = new float3(x / (float) (newResolution.x - 1) - 0.5f,
                                y / (float) (newResolution.y - 1) - 0.5f, z / (float) (newResolution.z - 1) - 0.5f);
                        }
                    }
                }
            }
        }

        public int GetIndex(int x, int y, int z)
        {
            return x + y * resolution.x + z * (resolution.x * resolution.y);
        }

        public int GetIndex(Vector3Int resolution, int x, int y, int z)
        {
            return x + y * resolution.x + z * (resolution.x * resolution.y);
        }

        public float3 GetControlPoint(int x, int y, int z)
        {
            var index = GetIndex(x, y, z);
            return controlPoints[index];
        }

        public void SetControlPoint(int x, int y, int z, float3 controlPoint)
        {
            var index = GetIndex(x, y, z);
            controlPoints[index] = controlPoint;
        }

        public override DataFlags DataFlags => DataFlags.Vertices;

        public override JobHandle Process(MeshData data, JobHandle dependency = default)
        {
            var meshToAxis = DeformerUtils.GetMeshToAxisSpace(Target, data.Target.GetTransform());

            return new LatticeJob
            {
                controlPoints = new NativeArray<float3>(controlPoints, Allocator.TempJob),
                resolution = new int3(resolution.x, resolution.y, resolution.z),
                meshToTarget = meshToAxis,
                targetToMesh = meshToAxis.inverse,
                vertices = data.DynamicNative.VertexBuffer
            }.Schedule(data.Length, DEFAULT_BATCH_COUNT, dependency);
        }

        [BurstCompile(CompileSynchronously = COMPILE_SYNCHRONOUSLY)]
        public struct LatticeJob : IJobParallelFor
        {
            [DeallocateOnJobCompletion, ReadOnly] public NativeArray<float3> controlPoints;
            [ReadOnly] public int3 resolution;
            [ReadOnly] public float4x4 meshToTarget;
            [ReadOnly] public float4x4 targetToMesh;
            public NativeArray<float3> vertices;

            public void Execute(int index)
            {
                // Convert from [-0.5,0.5] space to [0,1]
                var sourcePosition = transform(meshToTarget, vertices[index]) + float3(0.5f, 0.5f, 0.5f);

                // Determine the negative corner of the lattice cell containing the source position
                var negativeCorner = new int3((int) (sourcePosition.x * (resolution.x - 1)), (int) (sourcePosition.y * (resolution.y - 1)), (int) (sourcePosition.z * (resolution.z - 1)));

                // Clamp the corner to an acceptable range in case the source vertex is outside or on the lattice bounds
                negativeCorner = max(negativeCorner, new int3(0, 0, 0));
                negativeCorner = min(negativeCorner, resolution - new int3(2, 2, 2));

                int index0 = (negativeCorner.x + 0) + (negativeCorner.y + 0) * resolution.x + (negativeCorner.z + 0) * (resolution.x * resolution.y);
                int index1 = (negativeCorner.x + 1) + (negativeCorner.y + 0) * resolution.x + (negativeCorner.z + 0) * (resolution.x * resolution.y);
                int index2 = (negativeCorner.x + 0) + (negativeCorner.y + 1) * resolution.x + (negativeCorner.z + 0) * (resolution.x * resolution.y);
                int index3 = (negativeCorner.x + 1) + (negativeCorner.y + 1) * resolution.x + (negativeCorner.z + 0) * (resolution.x * resolution.y);
                int index4 = (negativeCorner.x + 0) + (negativeCorner.y + 0) * resolution.x + (negativeCorner.z + 1) * (resolution.x * resolution.y);
                int index5 = (negativeCorner.x + 1) + (negativeCorner.y + 0) * resolution.x + (negativeCorner.z + 1) * (resolution.x * resolution.y);
                int index6 = (negativeCorner.x + 0) + (negativeCorner.y + 1) * resolution.x + (negativeCorner.z + 1) * (resolution.x * resolution.y);
                int index7 = (negativeCorner.x + 1) + (negativeCorner.y + 1) * resolution.x + (negativeCorner.z + 1) * (resolution.x * resolution.y);

                var localizedSourcePosition = (sourcePosition) * (resolution - new int3(1, 1, 1)) - negativeCorner;

                // Clamp the local position outside of the bounds so that our interpolation outside the lattice is clamped
                localizedSourcePosition = clamp(localizedSourcePosition, float3.zero, new float3(1, 1, 1));

                var newPosition = float3.zero;

                // X-Axis
                if (sourcePosition.x < 0)
                {
                    // Outside of lattice (negative in axis)
                    var min1 = lerp(controlPoints[index0].x, controlPoints[index2].x, localizedSourcePosition.y);
                    var min2 = lerp(controlPoints[index4].x, controlPoints[index6].x, localizedSourcePosition.y);
                    var min = lerp(min1, min2, localizedSourcePosition.z);
                    newPosition.x = sourcePosition.x + min;
                }
                else if (sourcePosition.x > 1)
                {
                    // Outside of lattice (positive in axis)
                    var max1 = lerp(controlPoints[index1].x, controlPoints[index3].x, localizedSourcePosition.y);
                    var max2 = lerp(controlPoints[index5].x, controlPoints[index7].x, localizedSourcePosition.y);
                    var max = lerp(max1, max2, localizedSourcePosition.z);
                    newPosition.x = sourcePosition.x + max - 1;
                }
                else
                {
                    // Inside lattice
                    var min1 = lerp(controlPoints[index0].x, controlPoints[index2].x, localizedSourcePosition.y);
                    var max1 = lerp(controlPoints[index1].x, controlPoints[index3].x, localizedSourcePosition.y);

                    var min2 = lerp(controlPoints[index4].x, controlPoints[index6].x, localizedSourcePosition.y);
                    var max2 = lerp(controlPoints[index5].x, controlPoints[index7].x, localizedSourcePosition.y);

                    var min = lerp(min1, min2, localizedSourcePosition.z);
                    var max = lerp(max1, max2, localizedSourcePosition.z);
                    newPosition.x = lerp(min, max, localizedSourcePosition.x);
                }

                // Y-Axis
                if (sourcePosition.y < 0)
                {
                    // Outside of lattice (negative in axis)
                    var min1 = lerp(controlPoints[index0].y, controlPoints[index1].y, localizedSourcePosition.x);
                    var min2 = lerp(controlPoints[index4].y, controlPoints[index5].y, localizedSourcePosition.x);
                    var min = lerp(min1, min2, localizedSourcePosition.z);
                    newPosition.y = sourcePosition.y + min;
                }
                else if (sourcePosition.y > 1)
                {
                    // Outside of lattice (positive in axis)
                    var max1 = lerp(controlPoints[index2].y, controlPoints[index3].y, localizedSourcePosition.x);
                    var max2 = lerp(controlPoints[index6].y, controlPoints[index7].y, localizedSourcePosition.x);
                    var max = lerp(max1, max2, localizedSourcePosition.z);
                    newPosition.y = sourcePosition.y + max - 1;
                }
                else
                {
                    var min1 = lerp(controlPoints[index0].y, controlPoints[index1].y, localizedSourcePosition.x);
                    var max1 = lerp(controlPoints[index2].y, controlPoints[index3].y, localizedSourcePosition.x);

                    var min2 = lerp(controlPoints[index4].y, controlPoints[index5].y, localizedSourcePosition.x);
                    var max2 = lerp(controlPoints[index6].y, controlPoints[index7].y, localizedSourcePosition.x);

                    var min = lerp(min1, min2, localizedSourcePosition.z);
                    var max = lerp(max1, max2, localizedSourcePosition.z);

                    newPosition.y = lerp(min, max, localizedSourcePosition.y);
                }

                // Z-Axis
                if (sourcePosition.z < 0)
                {
                    // Outside of lattice (negative in axis)
                    var min1 = lerp(controlPoints[index0].z, controlPoints[index1].z, localizedSourcePosition.x);
                    var min2 = lerp(controlPoints[index2].z, controlPoints[index3].z, localizedSourcePosition.x);
                    var min = lerp(min1, min2, localizedSourcePosition.y);
                    newPosition.z = sourcePosition.z + min;
                }
                else if (sourcePosition.z > 1)
                {
                    // Outside of lattice (positive in axis)
                    var max1 = lerp(controlPoints[index4].z, controlPoints[index5].z, localizedSourcePosition.x);
                    var max2 = lerp(controlPoints[index6].z, controlPoints[index7].z, localizedSourcePosition.x);
                    var max = lerp(max1, max2, localizedSourcePosition.y);
                    newPosition.z = sourcePosition.z + max - 1;
                }
                else
                {
                    var min1 = lerp(controlPoints[index0].z, controlPoints[index1].z, localizedSourcePosition.x);
                    var max1 = lerp(controlPoints[index4].z, controlPoints[index5].z, localizedSourcePosition.x);

                    var min2 = lerp(controlPoints[index2].z, controlPoints[index3].z, localizedSourcePosition.x);
                    var max2 = lerp(controlPoints[index6].z, controlPoints[index7].z, localizedSourcePosition.x);

                    var min = lerp(min1, min2, localizedSourcePosition.y);
                    var max = lerp(max1, max2, localizedSourcePosition.y);

                    newPosition.z = lerp(min, max, localizedSourcePosition.z);
                }

                vertices[index] = transform(targetToMesh, newPosition);
            }
        }
    }
}