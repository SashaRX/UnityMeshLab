// Developer-only source capture; copy into a scratch project Assets/Editor.
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;
public static class SourceSignalsCapture {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static readonly Assembly A=typeof(RemeshSettings).Assembly;
 static object Get(object g,string n)=>g.GetType().GetField(n,F).GetValue(g);
 public static void Run(){GameObject root=null;try{
  root=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Environment.GetEnvironmentVariable("MESHLAB_SIGNAL_SOURCE_ASSET") ?? "Assets/Bust.fbx"));
  var capture=A.GetType("SashaRX.UnityMeshLab.RemeshSource").GetMethod("Capture",F,null,new[]{typeof(Matrix4x4),typeof(IList<Renderer>),typeof(bool),typeof(bool),typeof(bool),typeof(RemeshSettings),typeof(bool)},null);
  var source=capture.Invoke(null,new object[]{root.transform.worldToLocalMatrix,root.GetComponentsInChildren<Renderer>(),true,true,false,null,false});
  var p=(Vector3[])Get(source,"positions");var n=(Vector3[])Get(source,"normals");var ix=(int[])Get(source,"indices");
  Vector3 low=p[0],high=p[0];foreach(var v in p){low=Vector3.Min(low,v);high=Vector3.Max(high,v);}float size=(high-low).magnitude;var center=(high+low)*.5f;
  Debug.Log("SOURCE_CAPTURE vertices="+p.Length+" faces="+ix.Length/3+" bounds="+low+" .. "+high+" diagonal="+size);
  string dir=Path.GetFullPath(Path.Combine(Application.dataPath,"../source-signals"));Directory.CreateDirectory(dir);
  File.WriteAllText(Path.Combine(dir,"source-space.json"),JsonUtility.ToJson(new Vector4(center.x,center.y,center.z,size)));
  using(var w=new BinaryWriter(File.Create(Path.Combine(dir,"source.bin")))){w.Write(p.Length);w.Write(ix.Length);foreach(var v in p){var q=(v-center)/size;w.Write(q.x);w.Write(q.y);w.Write(q.z);}foreach(var v in n){w.Write(v.x);w.Write(v.y);w.Write(v.z);}foreach(int i in ix)w.Write(i);}
  UnityEngine.Object.DestroyImmediate(root);root=null;EditorApplication.Exit(0);
 }catch(Exception e){Debug.LogException(e);if(root)UnityEngine.Object.DestroyImmediate(root);EditorApplication.Exit(1);}}
}
