using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        internal static GroupedShellTransfer.TransferResult DescribeForwardProjection(Mesh target,Mesh source,int[] sources)
        {
            var shells=UvShellExtractor.Extract(target.uv,target.triangles);
            var donorShells=UvShellExtractor.Extract(source.uv,source.triangles);
            var donorPositions=source.vertices;
            var positions=target.vertices;var indices=target.triangles;var uv=target.uv2;
            var result=new GroupedShellTransfer.TransferResult {
                uv2=uv,vertexToSourceShell=sources,verticesTotal=positions.Length,verticesTransferred=sources.Count(v=>v>=0),
                targetShellToSourceShell=new int[shells.Count],targetShellMethod=new int[shells.Count],
                targetShellCentroids=new Vector3[shells.Count],targetShellMatchDistSqr=new float[shells.Count],
                targetShellStatus=new GroupedShellTransfer.ShellStatus[shells.Count],targetShellIssues=new int[shells.Count],
                faceToTargetShell=new int[indices.Length/3]
            };
            foreach(var shell in shells)
            {
                int id=shell.shellId;
                var labels=shell.vertexIndices.Select(v=>sources[v]).Where(v=>v>=0).GroupBy(v=>v)
                    .OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key).ToArray();
                int donor=labels.Length==0?-1:labels[0].Key;
                result.targetShellToSourceShell[id]=donor;
                result.targetShellMethod[id]=labels.Length>1?2:0;
                if(donor<0) ++result.shellsUnmatched;else ++result.shellsMatched;
                if(labels.Length>1) ++result.shellsMerged;else ++result.shellsInterpolation;
                result.targetShellCentroids[id]=shell.vertexIndices.Aggregate(Vector3.zero,(sum,v)=>sum+positions[v])/shell.vertexIndices.Count;
                if(donor>=0)
                {
                    var group=donorShells[donor];
                    var center=group.vertexIndices.Aggregate(Vector3.zero,(sum,v)=>sum+donorPositions[v])/group.vertexIndices.Count;
                    result.targetShellMatchDistSqr[id]=(result.targetShellCentroids[id]-center).sqrMagnitude;
                }
                var quality=TransferCandidateQuality.Measure(shell,indices,positions,shell.vertexIndices.ToDictionary(v=>v,v=>uv[v]));
                int issues=Math.Max(quality.issues,quality.stretchedFaces+quality.invalidFaces);
                if(quality.overlapPairs>0 || !quality.overlapScanComplete) issues=Math.Max(issues,1);
                result.targetShellIssues[id]=issues;
                result.targetShellStatus[id]=donor<0?GroupedShellTransfer.ShellStatus.Unmatched:
                    issues==0?GroupedShellTransfer.ShellStatus.Accepted:
                    issues<=shell.faceIndices.Count*.3?GroupedShellTransfer.ShellStatus.Degraded:GroupedShellTransfer.ShellStatus.Poor;
                foreach(int face in shell.faceIndices) result.faceToTargetShell[face]=id;
            }
            return result;
        }

        /// <summary>Subdivide a detached forward target at donor chart seams.
        /// Existing UV0 is interpolated, and no donor atlas coordinates change.
        /// The caller must compare the projected result before publication.</summary>
        internal static Mesh PrepareForwardSeams(Mesh source,Mesh target,Vector2[] legacyUv,int[] legacySources,int resolution,
            out int[] vertexSourceShells,Matrix4x4? targetLocalToWorld=null)
        {
            var prepared=PrepareForwardSeamsAsync(source,target,legacyUv,legacySources,resolution,false,targetLocalToWorld:targetLocalToWorld).GetAwaiter().GetResult();
            vertexSourceShells=prepared.sources;
            return prepared.mesh;
        }

        internal static async Task<(Mesh mesh,int[] sources)> PrepareForwardSeamsAsync(Mesh source,Mesh target,
            Vector2[] legacyUv,int[] legacySources,int resolution,bool useAsync,CancellationToken token=default,
            HashSet<int> protectedFaces=null,int attempt=0,TransferMatchTrace trace=null,float boundaryToleranceTexels=UvTransferTolerance.BoundaryTexels,
            Matrix4x4? targetLocalToWorld=null,TransferPerformanceProfile performance=null,bool shareSeamPasses=true)
        {
            var renderedFrame=targetLocalToWorld ?? Matrix4x4.identity;
            // Without sharing (the regression control) every pass prepares its own prefix.
            using var shared=shareSeamPasses?new ForwardSeamShared():null;
            TransferUvQuality Quality(Mesh mesh,Vector2[] uv)
            {
                using(performance?.Accumulated("Forward seam quality measures"))
                    return TransferUvQuality.Measure(mesh,uv,Vector2.one,renderedFrame);
            }
            var stable=await PrepareForwardSeamsCore(source,target,legacyUv,legacySources,resolution,useAsync,token,
                protectedFaces,attempt,trace,false,false,boundaryToleranceTexels,renderedFrame,performance,shared);
            if (stable.mesh && Quality(stable.mesh,stable.mesh.uv2).worstAnisotropy<=4)
                return stable;
            foreach(bool existingAtlasOnly in new[] {false,true})
            {
                Mesh candidate=null;
                try
                {
                    var incremental=await PrepareForwardSeamsCore(source,target,legacyUv,legacySources,resolution,useAsync,token,
                        protectedFaces,attempt,trace,true,existingAtlasOnly,boundaryToleranceTexels,renderedFrame,performance,shared);
                    candidate=incremental.mesh;
                    if (!candidate) continue;
                    var after=Quality(candidate,candidate.uv2);
                    var before=Quality(stable.mesh?stable.mesh:target,stable.mesh?stable.mesh.uv2:legacyUv);
                    if (after.overlapScanComplete && after.invalidFaces<=before.invalidFaces && after.degenerateFaces<=before.degenerateFaces
                        && after.outOfBoundsVertices<=before.outOfBoundsVertices && after.overlapPairArea<=before.overlapPairArea*1.001+1e-14
                        && UvTransferTolerance.MetricFits(after.worstAnisotropy,before.worstAnisotropy)
                        && (after.degenerateFaces<before.degenerateFaces || after.areaWeightedAnisotropy<before.areaWeightedAnisotropy*.999999))
                    {
                        if (stable.mesh) UnityEngine.Object.DestroyImmediate(stable.mesh);
                        candidate=null;stable=incremental;
                        if(after.worstAnisotropy<=4) return stable;
                    }
                }
                catch (InvalidOperationException error)
                {
                    if (trace!=null) trace.forwardSeamRefusal="Incremental seam trial: "+error.Message;
                }
                catch
                {
                    if (stable.mesh) UnityEngine.Object.DestroyImmediate(stable.mesh);
                    throw;
                }
                finally {if(candidate) UnityEngine.Object.DestroyImmediate(candidate);}
            }
            return stable;
        }

        // Everything a seam pass computes before its own options take effect:
        // clean inputs, donor charts and the initial face projection with its
        // first corner snap. Passes over one target differ only in cut
        // validation, the existing-atlas policy and protected faces, which none
        // of these read; each pass continues on its own copy of the projection.
        sealed class ForwardSeamShared : IDisposable
        {
            internal bool prepared, unusable;
            internal ReverseUvInputs inputs;
            internal Mesh donorMesh;
            internal Surface[] donor;
            internal List<Chart> charts;
            internal List<int> validFaces;
            internal Surface projected;
            internal long queries;
            // Captured on the editor thread: Unity mesh accessors are main-thread only.
            Surface pristineTarget;
            DonorSeamIndex seamIndex;
            int[] targetIndices, sourceFaceShells;
            Vector3[] targetPositions;
            bool legacyAudited;
            HashSet<(int,int)> legacyContacts;

            internal int[] TargetIndices(Mesh target) => targetIndices ??= target.triangles;
            internal Vector3[] TargetPositions(Mesh target) => targetPositions ??= target.vertices;

            internal int[] SourceFaceShells(Mesh source)
            {
                if (sourceFaceShells!=null) return sourceFaceShells;
                var shells=UvShellExtractor.Extract(source.uv,source.triangles);
                var faceShells=new int[source.triangles.Length/3];
                foreach(var shell in shells) foreach(int f in shell.faceIndices) faceShells[f]=shell.shellId;
                return sourceFaceShells=faceShells;
            }

            /// <summary>Contacts the legacy layout already has, as ordered face pairs;
            /// null when that audit is incomplete. Every pass audits the same legacy UVs.</summary>
            internal HashSet<(int,int)> LegacyContacts(Vector2[] legacyUv,int[] indices,CancellationToken token,TransferPerformanceProfile performance)
            {
                if (legacyAudited) return legacyContacts;
                UvAtlasDiagnostics.Report report;
                using(performance?.Accumulated("Forward seam contact audits"))
                    report=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=legacyUv,indices=indices,
                        charts=new int[legacyUv.Length]},token,comparisonBudget:2000000,collectConflicts:true);
                legacyContacts=report.complete?new HashSet<(int,int)>(report.conflicts.Select(p=>(Math.Min(p.a,p.b),Math.Max(p.a,p.b)))):null;
                legacyAudited=true;
                return legacyContacts;
            }

            // Built on first use, inside the first pass's projection work.
            internal DonorSeamIndex SeamIndex(Options options,CancellationToken token)
            {
                if (seamIndex==null)
                    using (options.performance?.Accumulated("Projection donor seam index")) seamIndex=DonorSeamIndex.Create(donor,token);
                return seamIndex;
            }

            internal void Prepare(Mesh source,Mesh target,int resolution,Matrix4x4 renderedFrame,CancellationToken token)
            {
                inputs=ReverseUvInputs.Prepare(new[] {new Level {lod=1,inputs=new[] {new Input {mesh=source,key=source.name}}},
                    new Level {lod=0,inputs=new[] {new Input {mesh=target,key=target.name}}}},token);
                var cleanSource=inputs.levels[0].inputs[0].mesh;
                var vertices=cleanSource.vertices;var indices=cleanSource.triangles;var uv=cleanSource.uv2;
                if (uv.Length!=vertices.Length) {unusable=prepared=true;return;}
                var valid=new List<int>(); validFaces=new List<int>();
                for(int t=0;t<indices.Length;t+=3)
                    if (ForwardAnisotropy(vertices[indices[t]],vertices[indices[t+1]],vertices[indices[t+2]],
                        uv[indices[t]],uv[indices[t+1]],uv[indices[t+2]],renderedFrame)<=4)
                    {valid.AddRange(new[] {indices[t],indices[t+1],indices[t+2]});validFaces.Add(inputs.levels[0].inputs[0].sourceFaces[t/3]);}
                if (valid.Count==0) {unusable=prepared=true;return;}
                donorMesh=UnityEngine.Object.Instantiate(cleanSource);
                donorMesh.subMeshCount=1;donorMesh.triangles=valid.ToArray();
                donor=Capture(new Level {lod=1,inputs=new[] {new Input {mesh=donorMesh,key=source.name}}},Vector3.zero);
                int next=0;
                foreach(var s in donor)
                {
                    for(int i=0;i<s.indices.Length;++i) s.pixels[i]=uv[s.indices[i]]*resolution;
                    AssignCharts(s,Enumerable.Range(0,s.faces.Length).ToArray(),ref next);
                }
                pristineTarget=Capture(inputs.levels[1],Vector3.zero)[0];
                // Only a completed preparation is reused by later passes.
                prepared=true;
            }

            // Runs once, off the editor thread when asynchronous. A failed or
            // cancelled run caches nothing, so the next pass repeats it.
            internal void ProjectInitialShared(Options options,CancellationToken token)
            {
                List<Chart> built;
                using (options.performance?.Accumulated("Projection donor charts")) built=Charts(donor);
                // A copy, so a failed or cancelled run leaves the capture untouched.
                var targets=new[] {CopySurface(pristineTarget)};
                long count=0;
                ReverseUvTransfer.ProjectInitial(donor,targets,built,options,ref count,token);
                charts=built;queries=count;projected=targets[0];
            }

            // Every array a pass may write in place is copied. Faces carry no
            // overlap relations before the pass resolves overlaps itself.
            internal Surface CopyProjected() => CopySurface(projected);

            static Surface CopySurface(Surface s)
            {
                var faces=new Face[s.faces.Length];
                for(int f=0;f<faces.Length;++f)
                {
                    if(s.faces[f].overlapParents!=null) throw new InvalidOperationException("Shared seam projection cannot copy overlap relations.");
                    faces[f]=s.faces[f].Copy();
                }
                return new Surface {input=s.input,positions=Copy(s.positions),indices=Copy(s.indices),
                    sourcePositions=Copy(s.sourcePositions),sourceIndices=Copy(s.sourceIndices),sourceUvCorners=Copy(s.sourceUvCorners),
                    preparedRegions=Copy(s.preparedRegions),pixels=Copy(s.pixels),packingCoordinates=Copy(s.packingCoordinates),
                    faces=faces,lod=s.lod,node=s.node,orientation=s.orientation,sourceFaces=Copy(s.sourceFaces),
                    sourceBarycentrics=Copy(s.sourceBarycentrics),splitSourceFaces=s.splitSourceFaces,
                    packingFrames=Copy(s.packingFrames),metricFrames=Copy(s.metricFrames)};
            }

            static T[] Copy<T>(T[] values) => values==null?null:(T[])values.Clone();

            public void Dispose()
            {
                if (donorMesh) UnityEngine.Object.DestroyImmediate(donorMesh);
                inputs?.Dispose();
            }
        }

        static async Task RunProjection(Action<CancellationToken> work,bool useAsync,CancellationToken token)
        {
            if (!useAsync) {work(token);return;}
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
            var projection=Task.Run(()=>work(cancellation.Token),cancellation.Token);
            while(!projection.IsCompleted)
            {
                if(UvProgress.CancelRequested) cancellation.Cancel();
                // Keep polling until the cancelled worker exits before disposing its donor data.
                await Task.WhenAny(projection,Task.Delay(25,CancellationToken.None));
            }
            await projection;
        }

        static async Task<(Mesh mesh,int[] sources)> PrepareForwardSeamsCore(Mesh source,Mesh target,
            Vector2[] legacyUv,int[] legacySources,int resolution,bool useAsync,CancellationToken token,
            HashSet<int> protectedFaces,int attempt,TransferMatchTrace trace,bool incrementalCuts,bool existingAtlasOnly,float boundaryToleranceTexels,
            Matrix4x4 renderedFrame,TransferPerformanceProfile performance,ForwardSeamShared shared)
        {
            using var owned=shared==null?new ForwardSeamShared():null;
            var pass=shared ?? owned;
            if (!pass.prepared) pass.Prepare(source,target,resolution,renderedFrame,token);
            if (pass.unusable) return (null,null);
            // One count per pass that reaches projection, including protected-face retries.
            performance?.Accumulate("Forward seam passes",0,1);
            var inputs=pass.inputs;var validFaces=pass.validFaces;
            var options=new Options {seedResolution=resolution,maxAtlasSize=resolution,projectionReach=.05f,
                splitDonorSeams=true,preserveProjectedOverlap=false,comparisonBudget=20000000,
                validateCutsIncrementally=incrementalCuts,projectExistingAtlasOnly=existingAtlasOnly,
                boundaryToleranceTexels=boundaryToleranceTexels,stretchFrame=renderedFrame,performance=performance};
            if (protectedFaces != null)
                options.seamCutExclusions = new Dictionary<(int,int),HashSet<int>> {[(0,0)] = protectedFaces};
            Surface[] targets;
            using(performance?.Accumulated("Forward seam projection"))
            {
                if (pass.projected==null) await RunProjection(t=>pass.ProjectInitialShared(options,t),useAsync,token);
                targets=new[] {pass.CopyProjected()};
                await RunProjection(t=>ProjectAfterInitial(pass.donor,targets,pass.charts,options,pass.queries,t,pass.SeamIndex(options,t)),useAsync,token);
            }
            var surface=targets[0];
            if (!surface.faces.Any(f=>f.inherited)) return (null,null);
            var originalIndices=pass.TargetIndices(target);
            var originalPositions=pass.TargetPositions(target);
            var cornerUv=new Vector2[surface.pixels.Length];
            var sourceFaceShells=pass.SourceFaceShells(source);
            var cornerSources=new int[cornerUv.Length];
            var originalFaces=new int[surface.faces.Length];var projectedFaces=new HashSet<int>();
            for(int f=0;f<surface.faces.Length;++f)
            {
                int cleanFace=surface.sourceFaces[f];
                int originalFace=inputs.levels[1].inputs[0].sourceFaces[cleanFace];
                originalFaces[f]=originalFace;
                int old=originalFace*3,start=f*3;
                double oldMetric=ForwardAnisotropy(originalPositions[originalIndices[old]],originalPositions[originalIndices[old+1]],
                    originalPositions[originalIndices[old+2]],legacyUv[originalIndices[old]],legacyUv[originalIndices[old+1]],legacyUv[originalIndices[old+2]],renderedFrame);
                double metric=ForwardAnisotropy(surface.positions[surface.indices[start]],surface.positions[surface.indices[start+1]],
                    surface.positions[surface.indices[start+2]],surface.pixels[start],surface.pixels[start+1],surface.pixels[start+2],renderedFrame);
                if (trace != null && oldMetric > 4)
                {
                    if (trace.forwardFaces.Count < 4096)
                        trace.forwardFaces.Add(new TransferMatchTrace.ForwardFace {sourceFace=originalFace,piece=f,attempt=attempt,
                            parentFace=surface.faces[f].parentFace,inherited=surface.faces[f].inherited,incrementalCuts=incrementalCuts,
                            existingAtlasOnly=existingAtlasOnly,
                            legacyAnisotropy=oldMetric,projectedAnisotropy=metric,
                            rejection=surface.faces[f].rejection,cutRefusal=surface.faces[f].cutRefusal});
                    else trace.truncated = true;
                }
                if (!UvTransferTolerance.MetricFits(metric,oldMetric)) surface.faces[f].inherited=false;
                if(surface.faces[f].inherited) projectedFaces.Add(f);
                for(int k=0;k<3;++k)
                {
                    int corner=f*3+k;
                    var bary=surface.sourceBarycentrics==null ? k==0?Vector3.right:k==1?Vector3.up:Vector3.forward : surface.sourceBarycentrics[corner];
                    int oldCorner=bary.x>=bary.y && bary.x>=bary.z?0:bary.y>=bary.z?1:2;
                    cornerSources[corner]=surface.faces[f].inherited?sourceFaceShells[validFaces[surface.faces[f].parentFace]]:
                        legacySources[originalIndices[originalFace*3+oldCorner]];
                    cornerUv[corner]=surface.faces[f].inherited?surface.pixels[corner]/resolution:
                        legacyUv[originalIndices[originalFace*3]]*bary.x+legacyUv[originalIndices[originalFace*3+1]]*bary.y
                            +legacyUv[originalIndices[originalFace*3+2]]*bary.z;
                }
            }
            var allowed=pass.LegacyContacts(legacyUv,originalIndices,token,performance);
            if (allowed==null) return (null,null);
            while(true)
            {
                token.ThrowIfCancellationRequested();
                if(UvProgress.CancelRequested) throw new OperationCanceledException();
                UvAtlasDiagnostics.Report scan;
                using(performance?.Accumulated("Forward seam contact audits"))
                    scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=cornerUv,
                        indices=Enumerable.Range(0,cornerUv.Length).ToArray(),charts=new int[cornerUv.Length]},token,comparisonBudget:2000000,collectConflicts:true);
                if(!scan.complete) return (null,null);
                var refused=new HashSet<int>();
                foreach(var contact in scan.conflicts)
                {
                    int a=originalFaces[contact.a],b=originalFaces[contact.b];
                    if(!allowed.Contains((Math.Min(a,b),Math.Max(a,b)))) {refused.Add(contact.a);refused.Add(contact.b);}
                }
                if(refused.Count==0) break;
                var legacyContacts = refused.Where(f=>!projectedFaces.Contains(f)).ToArray();
                refused.IntersectWith(projectedFaces);
                if(refused.Count==0)
                {
                    // Cutting a nearly collapsed legacy face can introduce
                    // Float32 contacts even after its projection is rolled
                    // back. Protect that face and its incident edges, then
                    // retry the remaining donor seams independently.
                    if (attempt >= 7) return (null,null);
                    protectedFaces ??= new HashSet<int>();
                    bool extended = false;
                    foreach(int face in legacyContacts) extended |= protectedFaces.Add(originalFaces[face]);
                    if (!extended) return (null,null);
                    return await PrepareForwardSeamsCore(source,target,legacyUv,legacySources,resolution,useAsync,token,protectedFaces,attempt+1,trace,incrementalCuts,existingAtlasOnly,boundaryToleranceTexels,renderedFrame,performance,shared);
                }
                for(int f=0;f<surface.faces.Length;++f)
                    if(refused.Contains(f))
                        for(int k=0;k<3;++k)
                        {
                            int corner=f*3+k,original=originalFaces[f]*3;
                            var bary=surface.sourceBarycentrics==null?k==0?Vector3.right:k==1?Vector3.up:Vector3.forward:surface.sourceBarycentrics[corner];
                            cornerUv[corner]=legacyUv[originalIndices[original]]*bary.x+legacyUv[originalIndices[original+1]]*bary.y+legacyUv[originalIndices[original+2]]*bary.z;
                            int closest=bary.x>=bary.y && bary.x>=bary.z?0:bary.y>=bary.z?1:2;
                            cornerSources[corner]=legacySources[originalIndices[original+closest]];
                        }
                projectedFaces.ExceptWith(refused);
            }
            var metricFailures=new HashSet<int>();
            for(int f=0;f<surface.faces.Length;++f)
            {
                int old=originalFaces[f]*3,t=f*3;
                double previous=ForwardAnisotropy(originalPositions[originalIndices[old]],originalPositions[originalIndices[old+1]],
                    originalPositions[originalIndices[old+2]],legacyUv[originalIndices[old]],legacyUv[originalIndices[old+1]],legacyUv[originalIndices[old+2]],renderedFrame);
                double current=ForwardAnisotropy(surface.positions[surface.indices[t]],surface.positions[surface.indices[t+1]],
                    surface.positions[surface.indices[t+2]],cornerUv[t],cornerUv[t+1],cornerUv[t+2],renderedFrame);
                if(!UvTransferTolerance.MetricFits(current,previous)) metricFailures.Add(originalFaces[f]);
            }
            if(metricFailures.Count>0)
            {
                if(attempt>=7) return (null,null);
                protectedFaces ??= new HashSet<int>();
                bool extended=false;
                foreach(int face in metricFailures) extended |= protectedFaces.Add(face);
                if(!extended) return (null,null);
                return await PrepareForwardSeamsCore(source,target,legacyUv,legacySources,resolution,useAsync,token,protectedFaces,attempt+1,trace,incrementalCuts,existingAtlasOnly,boundaryToleranceTexels,renderedFrame,performance,shared);
            }
            var vertexSources=new List<int>();
            var barycentrics=surface.sourceBarycentrics ?? Enumerable.Range(0,cornerUv.Length)
                .Select(i=>i%3==0?Vector3.right:i%3==1?Vector3.up:Vector3.forward).ToArray();
            var mesh=ReverseUvRefinedMesh.Copy(surface.input.mesh,surface.sourceFaces,barycentrics,cornerUv,
                (vertex,corner)=>vertexSources.Add(cornerSources[corner]));
            mesh.name=target.name+"_forwardSeams";
            return (mesh,vertexSources.ToArray());
        }

        // Transform edges rather than absolute positions: translation must not
        // introduce Float32 cancellation into the rendered stretch comparison.
        static double ForwardAnisotropy(Vector3 p,Vector3 q,Vector3 r,Vector2 a,Vector2 b,Vector2 c,Matrix4x4 renderedFrame)
            => TriangleAnisotropy(p,q,r,a,b,c,renderedFrame);
    }
}
