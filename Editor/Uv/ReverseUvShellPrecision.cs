using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Float32 cannot retain a micrometre-wide source face at an arbitrary
    // atlas offset. Spend at most 0.01 texel on a shared UV vertex, never cut
    // its incident faces into separate islands. Visible faces keep the normal
    // absolute stretch limit.
    internal static class ReverseUvShellPrecision
    {
        internal const double ErrorTexels = .01;

        internal static bool ResolutionLimited((double length,double x,double y) frame,
            Vector2 a,Vector2 b,Vector2 c)
        {
            double longest = Math.Sqrt(Math.Max(frame.length*frame.length,
                Math.Max(frame.x*frame.x+frame.y*frame.y,
                    (frame.x-frame.length)*(frame.x-frame.length)+frame.y*frame.y)));
            double uvLongest = Math.Max(Distance(a,b),Math.Max(Distance(a,c),Distance(b,c)));
            return longest>0 && frame.y>0 && uvLongest>0
                && frame.length*frame.y/longest*(uvLongest/longest)<=ErrorTexels;
        }

        internal static bool Fits((double length,double x,double y) frame,
            Vector2 a,Vector2 b,Vector2 c,double limit)
        {
            double metric=ReverseUvTransfer.TriangleAnisotropy(frame,a,b,c);
            if(double.IsNaN(metric) || double.IsInfinity(metric)) return false;
            if(metric<=limit) return true;
            return ResolutionLimited(frame,a,b,c) && Altitude(a,b,c)>0 && Altitude(a,b,c)<=ErrorTexels;
        }

        internal static bool Stabilize(Vector2[] source,Vector2[] placed,
            (double length,double x,double y)[] frames,double limit,CancellationToken token)
        {
            var initial=(Vector2[])placed.Clone();
            int orientation=1;
            for(int f=0;f<frames.Length;++f)
            {
                int t=f*3;double before=Area(source[t],source[t+1],source[t+2]);
                double after=Area(placed[t],placed[t+1],placed[t+2]);
                if(before!=0 && after!=0) {orientation=Math.Sign(before)*Math.Sign(after);break;}
            }
            var aliases=new Dictionary<Vector2,List<int>>();
            for(int c=0;c<source.Length;++c)
            {
                if(!aliases.TryGetValue(source[c],out var corners))
                {corners=new List<int>();aliases.Add(source[c],corners);}
                corners.Add(c);
            }
            for(int f=0;f<frames.Length;++f)
            {
                token.ThrowIfCancellationRequested();int t=f*3;
                if(Fits(frames[f],placed[t],placed[t+1],placed[t+2],limit)) continue;
                if(!ResolutionLimited(frames[f],placed[t],placed[t+1],placed[t+2])) return false;
                bool accepted=false;
                foreach(float step in new[] {.00001f,.0001f,.001f,.005f})
                {
                    for(int corner=0;corner<3 && !accepted;++corner)
                    {
                        foreach(var direction in new[] {Vector2.right,Vector2.left,Vector2.up,Vector2.down})
                        {
                            var connected=aliases[source[t+corner]];
                            var old=new Vector2[connected.Count];
                            var point=placed[t+corner]+direction*step;
                            bool bounded=true;
                            for(int i=0;i<connected.Count;++i)
                            {
                                int c=connected[i];old[i]=placed[c];
                                if(Distance(initial[c],point)>ErrorTexels) bounded=false;
                                placed[c]=point;
                            }
                            accepted=bounded;
                            foreach(int c in connected)
                            {
                                int n=c/3,j=n*3;
                                if(!Fits(frames[n],placed[j],placed[j+1],placed[j+2],limit)) accepted=false;
                                if(Math.Sign(Area(placed[j],placed[j+1],placed[j+2]))
                                    !=Math.Sign(Area(source[j],source[j+1],source[j+2]))*orientation) accepted=false;
                            }
                            if(accepted) break;
                            for(int i=0;i<connected.Count;++i) placed[connected[i]]=old[i];
                        }
                    }
                    if(accepted) break;
                }
                if(!accepted) return false;
            }
            return true;
        }

        static double Distance(Vector2 a,Vector2 b)
        {double x=(double)b.x-a.x,y=(double)b.y-a.y;return Math.Sqrt(x*x+y*y);}

        static double Area(Vector2 a,Vector2 b,Vector2 c)
            => ((double)b.x-a.x)*((double)c.y-a.y)-((double)b.y-a.y)*((double)c.x-a.x);

        static double Altitude(Vector2 a,Vector2 b,Vector2 c)
        {
            double longest=Math.Max(Distance(a,b),Math.Max(Distance(a,c),Distance(b,c)));
            return longest>0?Math.Abs(((double)b.x-a.x)*((double)c.y-a.y)
                -((double)b.y-a.y)*((double)c.x-a.x))/longest:0;
        }
    }
}
