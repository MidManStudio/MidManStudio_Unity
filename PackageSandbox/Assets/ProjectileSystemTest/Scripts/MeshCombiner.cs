using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class MeshCombiner : MonoBehaviour
{
	[Tooltip("If true, the original child game objects will be deactivated after combining.")]
	public bool deactivateOriginals = true;

	[Tooltip("If true, original child game objects will be completely destroyed (use with caution).")]
	public bool destroyOriginals = false;

	// Expose a button in the Unity Inspector Context Menu (right-click the component header)
	[ContextMenu("Combine Meshes Now")]
	public void CombineMeshes()
	{
		MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>();
		if (meshFilters.Length <= 1)
		{
			Debug.LogWarning("Not enough meshes to combine found in children.");
			return;
		}

		Dictionary<Material, List<CombineInstance>> materialToCombines = new Dictionary<Material, List<CombineInstance>>();
		List<Material> orderedMaterials = new List<Material>();
		Matrix4x4 localMatrix = transform.worldToLocalMatrix;

		int totalVertices = 0;

		foreach (MeshFilter mf in meshFilters)
		{
			if (mf.gameObject == gameObject) continue; // Skip the parent itself

			MeshRenderer mr = mf.GetComponent<MeshRenderer>();
			if (mr == null || mf.sharedMesh == null) continue;

			Material[] mats = mr.sharedMaterials;
			Mesh mesh = mf.sharedMesh;

			totalVertices += mesh.vertexCount;
			if (totalVertices > 65535)
			{
				Debug.LogWarning("Warning: Combined mesh exceeds 65,535 vertices. Consider splitting into smaller groups to support older hardware or ensure 32-bit index formats are enabled.");
			}

			Matrix4x4 matrix = localMatrix * mf.transform.localToWorldMatrix;

			for (int i = 0; i < mats.Length; i++)
			{
				if (mats[i] == null) continue;

				Material mat = mats[i];
				if (!materialToCombines.ContainsKey(mat))
				{
					materialToCombines.Add(mat, new List<CombineInstance>());
					orderedMaterials.Add(mat);
				}

				CombineInstance ci = new CombineInstance
				{
					mesh = mesh,
					subMeshIndex = Mathf.Min(i, mesh.subMeshCount - 1),
					transform = matrix
				};

				materialToCombines[mat].Add(ci);
			}
		}

		// Build final sub-meshes per material
		List<CombineInstance> finalCombines = new List<CombineInstance>();
		foreach (Material mat in orderedMaterials)
		{
			List<CombineInstance> combinesForMat = materialToCombines[mat];
			Mesh subMesh = new Mesh();
			subMesh.CombineMeshes(combinesForMat.ToArray(), true, true);

			CombineInstance finalCi = new CombineInstance
			{
				mesh = subMesh,
				subMeshIndex = 0,
				transform = Matrix4x4.identity
			};
			finalCombines.Add(finalCi);
		}

		// Create the final master mesh
		Mesh masterMesh = new Mesh();
		masterMesh.name = transform.name + "_CombinedMesh";
		masterMesh.CombineMeshes(finalCombines.ToArray(), false, false);

		// Apply mesh to local MeshFilter
		MeshFilter filter = GetComponent<MeshFilter>();
		filter.sharedMesh = masterMesh;

		// Apply materials to local MeshRenderer
		MeshRenderer renderer = GetComponent<MeshRenderer>();
		renderer.sharedMaterials = orderedMaterials.ToArray();

		// Handle original child objects
		foreach (MeshFilter mf in meshFilters)
		{
			if (mf.gameObject == gameObject) continue;

			if (destroyOriginals)
			{
				DestroyImmediate(mf.gameObject);
			}
			else if (deactivateOriginals)
			{
				mf.gameObject.SetActive(false);
			}
		}

		Debug.Log($"Successfully combined {meshFilters.Length - 1} meshes into one under '{gameObject.name}'!");
	}
}