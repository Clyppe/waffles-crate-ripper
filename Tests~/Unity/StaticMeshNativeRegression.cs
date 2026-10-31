using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

public static class StaticMeshNativeRegression
{
 const string Output="Assets/StaticRecoveryFixture";
 static int checks;
 static readonly List<string> passed=new List<string>();
 sealed class Snapshot
 {
  public string name; public int[] counts; public float windingSign; public Vector3[][] vertices,normals; public Vector2[][] uv,uv2; public Color[][] colors;
  public Material[] materials; public int vertexCount; public bool enabled;
 }
 public static void Run()
 {
  try
  {
   if(!Application.isBatchMode)throw new InvalidOperationException("Run this fixture in an isolated Unity batch project; it replaces the test scene.");
   checks=0;passed.Clear();
   var type=Type.GetType("Waffles.CrateRipper.StaticMeshRecovery, Waffles.CrateRipper.Editor",true);
   var repair=type.GetMethod("RepairFolder",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
   Need(repair!=null,"Static mesh recovery entry point exists");
   if(AssetDatabase.IsValidFolder(Output))AssetDatabase.DeleteAsset(Output);
   AssetDatabase.CreateFolder("Assets","StaticRecoveryFixture");
   var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
   var root=new GameObject("BatchRoot");root.transform.SetPositionAndRotation(new Vector3(8,2,-9),Quaternion.Euler(10,25,5));root.transform.localScale=new Vector3(1.3f,0.9f,1.2f);
   var mesh=MakeMesh();AssetDatabase.CreateAsset(mesh,Output+"/Original.asset");
   var materialA=new Material(Shader.Find("Standard"));var materialB=new Material(Shader.Find("Standard"));materialA.color=Color.red;materialB.color=Color.green;
   AssetDatabase.CreateAsset(materialA,Output+"/Red.mat");AssetDatabase.CreateAsset(materialB,Output+"/Green.mat");
   var objects=new[]{MakeObject(root,"First",mesh,new Vector3(2,1,3),Quaternion.Euler(20,40,15),new Vector3(1,2,0.5f),materialA,materialB),MakeObject(root,"Second",mesh,new Vector3(-2,3,1),Quaternion.Euler(13,22,41),new Vector3(2,0.7f,1.3f),materialA,materialB),MakeObject(root,"Mirrored",mesh,new Vector3(4,-1,-2),Quaternion.Euler(31,75,8),new Vector3(-1.5f,0.6f,1.7f),materialA,materialB),MakeObject(root,"Mirrored2",mesh,new Vector3(6,3,-1),Quaternion.Euler(12,35,17),new Vector3(-.7f,1.4f,.9f),materialA,materialB)};
   var original=objects.ToDictionary(o=>o.name,o=>Capture(o.GetComponent<MeshRenderer>()));
   StaticBatchingUtility.Combine(objects,root);
   foreach(var o in objects)Debug.Log("BATCH_STATE "+o.name+" "+o.GetComponent<MeshRenderer>().isPartOfStaticBatch);
   Need(objects.All(o=>o.GetComponent<MeshRenderer>().isPartOfStaticBatch),"Fixture uses real Unity static batching for normal and mirrored objects");
   var meshes=objects.Select(o=>o.GetComponent<MeshFilter>().sharedMesh).Distinct().ToArray();
   for(int i=0;i<meshes.Length;i++){meshes[i].hideFlags=HideFlags.None;AssetDatabase.CreateAsset(meshes[i],Output+"/Combined"+i+".asset");}
   objects[2].GetComponent<MeshRenderer>().enabled=false;
   var combined=objects.ToDictionary(o=>o.name,o=>Capture(o.GetComponent<MeshRenderer>()));
   foreach(var o in objects)ComparePositions(original[o.name],combined[o.name],"Combine preserves "+o.name+" geometry");
   var identity=new GameObject("IdentityBatch");var identityObjects=new[]{MakeObject(identity,"IdFirst",mesh,new Vector3(-7,1,4),Quaternion.Euler(0,60,0),Vector3.one,materialA,materialB),MakeObject(identity,"IdSecond",mesh,new Vector3(3,2,1),Quaternion.Euler(40,10,0),new Vector3(1,.5f,2),materialA,materialB)};StaticBatchingUtility.Combine(identityObjects,identity);var idMeshes=identityObjects.Select(o=>o.GetComponent<MeshFilter>().sharedMesh).Distinct().ToArray();for(int i=0;i<idMeshes.Length;i++){idMeshes[i].hideFlags=HideFlags.None;AssetDatabase.CreateAsset(idMeshes[i],Output+"/IdCombined"+i+".asset");}var idExpected=identityObjects.ToDictionary(o=>o.name,o=>Capture(o.GetComponent<MeshRenderer>()));
   var ordinary=MakeObject(root,"Ordinary",mesh,new Vector3(-5,0,4),Quaternion.identity,Vector3.one,materialA,materialB);ordinary.isStatic=false;
   var ordinaryMesh=ordinary.GetComponent<MeshFilter>().sharedMesh;
   var avatar=new GameObject("Avatar untouched");avatar.transform.SetParent(root.transform,false);var bone=new GameObject("Bone");bone.transform.SetParent(avatar.transform,false);var skin=avatar.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=mesh;skin.bones=new[]{bone.transform};skin.rootBone=bone.transform;skin.sharedMaterials=new[]{materialA,materialB};
   var prefabPath=Output+"/Batched.prefab";PrefabUtility.SaveAsPrefabAsset(root,prefabPath);
   var scenePath=Output+"/Batched.unity";EditorSceneManager.SaveScene(scene,scenePath);AssetDatabase.SaveAssets();
   EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
   var result=repair.Invoke(null,new object[]{Output,System.Threading.CancellationToken.None});Debug.Log("STATIC_REPAIR_RESULT "+JsonUtility.ToJson(result));
   AssetDatabase.SaveAssets();AssetDatabase.Refresh();
   scene=EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Single);
   root=scene.GetRootGameObjects().Single(o=>o.name=="BatchRoot");
   ValidateHierarchy(root,combined,ordinaryMesh,"scene");var repairedIdentity=scene.GetRootGameObjects().Single(o=>o.name=="IdentityBatch");ValidateHierarchy(repairedIdentity,idExpected,null,"identity",false);foreach(var idRenderer in repairedIdentity.GetComponentsInChildren<MeshRenderer>())VerifyMovement(idRenderer.gameObject);
   foreach(var o in root.GetComponentsInChildren<MeshRenderer>()){if(o.name=="Ordinary")continue;VerifyMovement(o.gameObject);}
   var movable=root.transform.Find("First").gameObject;var movablePath=Output+"/NewMovable.prefab";PrefabUtility.SaveAsPrefabAsset(movable,movablePath);var instance=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(movablePath));
   VerifyMovement(instance);Object.DestroyImmediate(instance);
   var prefab=PrefabUtility.LoadPrefabContents(prefabPath);try{ValidateHierarchy(prefab,combined,ordinaryMesh,"prefab");foreach(var renderer in prefab.GetComponentsInChildren<MeshRenderer>()){if(renderer.name!="Ordinary")VerifyMovement(renderer.gameObject);}}finally{PrefabUtility.UnloadPrefabContents(prefab);}
   var second=repair.Invoke(null,new object[]{Output,System.Threading.CancellationToken.None});Debug.Log("STATIC_REPAIR_SECOND "+JsonUtility.ToJson(second));
   var countField=second.GetType().GetField("repairedRenderers");if(countField!=null)Need(Convert.ToInt32(countField.GetValue(second))==0,"Repair is idempotent after recovered meshes are saved");
   WriteResult(true,null);Debug.Log("WAFFLES_STATIC_NATIVE_OK checks="+checks);
  }
  catch(Exception ex){WriteResult(false,ex.ToString());Debug.LogException(ex);throw;}
 }
 static void ValidateHierarchy(GameObject root,Dictionary<string,Snapshot> expected,Mesh ordinary,string label,bool checkExtras=true)
 {
  foreach(var pair in expected)
  {
   var renderer=root.transform.Find(pair.Key).GetComponent<MeshRenderer>();var so=new SerializedObject(renderer);
   Need(!renderer.isPartOfStaticBatch,label+" "+pair.Key+" no longer has a live static batch");
   Need(so.FindProperty("m_StaticBatchInfo.subMeshCount").intValue==0&&so.FindProperty("m_StaticBatchRoot").objectReferenceValue==null,label+" "+pair.Key+" static metadata cleared");
   var actual=Capture(renderer);ComparePositions(pair.Value,actual,label+" "+pair.Key+" visible world geometry preserved");
   Need(pair.Value.counts.SequenceEqual(actual.counts),label+" "+pair.Key+" material submesh index counts preserved");
   Need(actual.enabled==pair.Value.enabled,label+" "+pair.Key+" enabled state preserved");
   Need(pair.Value.materials.SequenceEqual(actual.materials),label+" "+pair.Key+" materials preserved");
   Need(actual.vertexCount<pair.Value.vertexCount,label+" "+pair.Key+" no longer contains unrelated combined vertices");
   CompareChannel(pair.Value,actual,label+" "+pair.Key);
  }
  if(!checkExtras)return;
  Need(root.transform.Find("Ordinary").GetComponent<MeshFilter>().sharedMesh==ordinary,label+" ordinary unbatched mesh unchanged");
  var skin=root.GetComponentInChildren<SkinnedMeshRenderer>();Need(skin!=null&&skin.sharedMesh==ordinary&&skin.bones.Length==1&&skin.rootBone==skin.bones[0],label+" avatar skin and original bone references preserved");
 }
 static Snapshot Capture(MeshRenderer renderer)
 {
  var mesh=renderer.GetComponent<MeshFilter>().sharedMesh;var so=new SerializedObject(renderer);int first=0,count=mesh.subMeshCount;var basis=renderer.transform.localToWorldMatrix;
  var info=so.FindProperty("m_StaticBatchInfo");if(info!=null&&info.FindPropertyRelative("subMeshCount").intValue>0){first=info.FindPropertyRelative("firstSubMesh").intValue;count=info.FindPropertyRelative("subMeshCount").intValue;var root=so.FindProperty("m_StaticBatchRoot").objectReferenceValue as Transform;basis=root?root.localToWorldMatrix:Matrix4x4.identity;}
  var result=new Snapshot{name=renderer.name,enabled=renderer.enabled,materials=renderer.sharedMaterials,vertexCount=mesh.vertexCount,windingSign=Mathf.Sign(basis.determinant),counts=new int[count],vertices=new Vector3[count][],normals=new Vector3[count][],uv=new Vector2[count][],uv2=new Vector2[count][],colors=new Color[count][]};
  var vertices=mesh.vertices;var normals=mesh.normals;var uv=mesh.uv;var uv2=mesh.uv2;var colors=mesh.colors;
  for(int s=0;s<count;s++){var indices=mesh.GetIndices(first+s,true);result.counts[s]=indices.Length;result.vertices[s]=indices.Select(i=>basis.MultiplyPoint3x4(vertices[i])).ToArray();result.normals[s]=indices.Select(i=>basis.inverse.transpose.MultiplyVector(normals[i]).normalized).ToArray();result.uv[s]=indices.Select(i=>uv[i]).ToArray();result.uv2[s]=indices.Select(i=>uv2[i]).ToArray();result.colors[s]=indices.Select(i=>colors[i]).ToArray();}
  return result;
 }
 static void ComparePositions(Snapshot expected,Snapshot actual,string message)
 {
  Need(expected.counts.SequenceEqual(actual.counts),message+" index layout");
  for(int s=0;s<expected.vertices.Length;s++)
  {
   var e=expected.vertices[s];var a=actual.vertices[s];var remaining=new List<Vector3>(a);
   foreach(var point in e){int index=remaining.FindIndex(v=>Vector3.Distance(v,point)<0.002f);if(index<0)throw new Exception(message+" changed triangle vertex "+point);remaining.RemoveAt(index);}
   for(int i=0;i<e.Length;i+=3){var center=(e[i]+e[i+1]+e[i+2])/3;int ai=-1;for(int j=0;j<a.Length;j+=3)if(Vector3.Distance(center,(a[j]+a[j+1]+a[j+2])/3)<.002f){ai=j;break;}if(ai<0)throw new Exception(message+" changed triangle grouping");var before=Vector3.Dot(Vector3.Cross(e[i+1]-e[i],e[i+2]-e[i]),expected.normals[s][i])*expected.windingSign;var after=Vector3.Dot(Vector3.Cross(a[ai+1]-a[ai],a[ai+2]-a[ai]),actual.normals[s][ai])*actual.windingSign;if(Mathf.Sign(before)!=Mathf.Sign(after))throw new Exception(message+" reversed face winding relative to vertex normals");}
  }
  Need(true,message+" with face orientation");
 }
 static void CompareChannel(Snapshot a,Snapshot b,string label)
 {
  for(int s=0;s<a.vertices.Length;s++)for(int i=0;i<a.vertices[s].Length;i++)
  {
   int j=Array.FindIndex(b.vertices[s],p=>Vector3.Distance(a.vertices[s][i],p)<.002f);if(j<0)throw new Exception(label+" missing channel vertex");
   if(Vector3.Distance(a.normals[s][i],b.normals[s][j])>0.003f)throw new Exception(label+" normal changed");if(Vector2.Distance(a.uv[s][i],b.uv[s][j])>0.0001f)throw new Exception(label+" UV0 changed");if(Vector2.Distance(a.uv2[s][i],b.uv2[s][j])>0.0001f)throw new Exception(label+" UV2 changed");if(a.colors[s][i]!=b.colors[s][j])throw new Exception(label+" color changed");
  }
  Need(true,label+" normals, UV0, lightmap UVs and vertex colors preserved");
 } static void VerifyMovement(GameObject obj)
 {
  var renderer=obj.GetComponent<MeshRenderer>();var mesh=obj.GetComponent<MeshFilter>().sharedMesh;var before=Capture(renderer);var matrix=obj.transform.localToWorldMatrix;var collider=obj.GetComponent<BoxCollider>();var localCenter=collider.center;var oldCollider=obj.transform.TransformPoint(localCenter);
  obj.transform.position+=new Vector3(4.7f,-2.4f,3.3f);obj.transform.rotation=Quaternion.Euler(15,70,-22)*obj.transform.rotation;var delta=obj.transform.localToWorldMatrix*matrix.inverse;var after=Capture(renderer);
  for(int s=0;s<before.vertices.Length;s++)for(int i=0;i<before.vertices[s].Length;i++)if(Vector3.Distance(delta.MultiplyPoint3x4(before.vertices[s][i]),after.vertices[s][i])>0.002f)throw new Exception(obj.name+" visual mesh failed to follow translation/rotation");
  Need(Vector3.Distance(delta.MultiplyPoint3x4(oldCollider),obj.transform.TransformPoint(collider.center))<0.001f,obj.name+" mesh and collider move together under translation and rotation");
  Need(renderer.bounds.size.sqrMagnitude>0&&renderer.bounds.Contains(obj.transform.TransformPoint(mesh.bounds.center)),obj.name+" renderer bounds follow the recovered mesh");
 }
 static GameObject MakeObject(GameObject root,string name,Mesh mesh,Vector3 position,Quaternion rotation,Vector3 scale,Material a,Material b)
 {
  var go=new GameObject(name);go.transform.SetParent(root.transform,false);go.transform.localPosition=position;go.transform.localRotation=rotation;go.transform.localScale=scale;go.AddComponent<MeshFilter>().sharedMesh=mesh;var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterials=new[]{a,b};var collider=go.AddComponent<BoxCollider>();collider.center=mesh.bounds.center;collider.size=mesh.bounds.size+Vector3.one*.1f;go.isStatic=true;return go;
 }
 static Mesh MakeMesh()
 {
  var mesh=new Mesh{name="Two separate quads"};mesh.vertices=new[]{new Vector3(-1,-1,0),new Vector3(0,-1,0),new Vector3(0,1,0),new Vector3(-1,1,0),new Vector3(0,-1,.2f),new Vector3(1,-1,.2f),new Vector3(1,1,.2f),new Vector3(0,1,.2f)};mesh.normals=Enumerable.Repeat(Vector3.back,8).ToArray();mesh.tangents=Enumerable.Repeat(new Vector4(1,0,0,-1),8).ToArray();mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up,Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.uv2=mesh.uv.Select(v=>v*.4f+Vector2.one*.1f).ToArray();mesh.colors=Enumerable.Range(0,8).Select(i=>new Color(i/8f,1-i/8f,.3f,1)).ToArray();mesh.subMeshCount=2;mesh.SetTriangles(new[]{0,2,1,0,3,2},0);mesh.SetTriangles(new[]{4,6,5,4,7,6},1);mesh.RecalculateBounds();return mesh;
 }
 static void Need(bool condition,string message){if(!condition)throw new Exception("Static recovery regression: "+message);checks++;passed.Add(message);Debug.Log("STATIC_NATIVE_PASS "+message);}
 static void WriteResult(bool success,string error){var escaped=error==null?"null":"\""+error.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n")+"\"";File.WriteAllText(Path.GetFullPath("../native-static-result.json"),"{\"success\":"+(success?"true":"false")+",\"checks\":"+checks+",\"error\":"+escaped+"}");}
}