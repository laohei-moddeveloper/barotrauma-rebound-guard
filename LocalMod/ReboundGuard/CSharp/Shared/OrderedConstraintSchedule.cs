using System;
using System.Threading.Tasks;

namespace ReboundGuard
{
    // Topological batching, NOT a reorder of constraints sharing a body.
    // Constraints in one batch have disjoint body indices. Consecutive batches
    // complete in order, preserving all original per-body dependencies.
    public sealed class OrderedConstraintSchedule
    {
        private int[] a=Array.Empty<int>(),b=Array.Empty<int>(),level=Array.Empty<int>(),last=Array.Empty<int>();
        private int[] offsets=Array.Empty<int>(),cursor=Array.Empty<int>(),ordered=Array.Empty<int>();
        private int count,bodyCount;
        private bool ready;
        public int Levels {get;private set;}
        public int MaximumWidth {get;private set;}
        public bool Refresh(int contactCount,int bodies,Func<int,int> readA,Func<int,int> readB)
        {
            bool same=ready&&count==contactCount&&bodyCount==bodies;
            if(same)
            {
                for(int i=0;i<count;i++)if(a[i]!=readA(i)||b[i]!=readB(i)){same=false;break;}
                if(same)return true;
            }
            Grow(ref a,contactCount);Grow(ref b,contactCount);
            for(int i=0;i<contactCount;i++){a[i]=readA(i);b[i]=readB(i);}
            return Build(contactCount,bodies);
        }
        public bool RefreshBulk(int contactCount,int bodies,Func<int[],int[],int,bool> matches,Action<int[],int[],int> copy)
        {
            if(ready&&count==contactCount&&bodyCount==bodies&&matches(a,b,contactCount))return true;
            Grow(ref a,contactCount);Grow(ref b,contactCount);copy(a,b,contactCount);
            return Build(contactCount,bodies);
        }
        private bool Build(int contactCount,int bodies)
        {
            count=contactCount;bodyCount=bodies;ready=false;Levels=MaximumWidth=0;
            Grow(ref a,count);Grow(ref b,count);Grow(ref level,count);Grow(ref ordered,count);
            Grow(ref offsets,count+2);Grow(ref cursor,count+2);Grow(ref last,bodies);
            Array.Fill(last,-1,0,bodies);
            for(int i=0;i<count;i++)
            {
                int left=a[i],right=b[i];
                if(left<0||right<0||left>=bodies||right>=bodies)return false;
                a[i]=left;b[i]=right;
                int next=Math.Max(last[left],last[right])+1;
                level[i]=next;last[left]=last[right]=next;
                Levels=Math.Max(Levels,next+1);
            }
            Array.Clear(offsets,0,Levels+2);
            for(int i=0;i<count;i++)offsets[level[i]+1]++;
            for(int i=0;i<Levels;i++)
            {
                MaximumWidth=Math.Max(MaximumWidth,offsets[i+1]);
                offsets[i+1]+=offsets[i];
            }
            Array.Copy(offsets,cursor,Levels+1);
            for(int i=0;i<count;i++)ordered[cursor[level[i]]++]=i;
            ready=true;return true;
        }
        public bool WorthParallel(int minimumCount,int minimumWidth)
        {
            return ready&&count>=minimumCount&&Levels>0&&(long)Levels*minimumWidth<=count;
        }
        public void Execute(Action<int,int> runRange,int workers,int minimumParallelWidth)
        {
            if(!ready)throw new InvalidOperationException("Constraint schedule is not ready");
            var options=new ParallelOptions{MaxDegreeOfParallelism=Math.Max(1,workers),TaskScheduler=TaskScheduler.Default};
            for(int batch=0;batch<Levels;batch++)
            {
                int from=offsets[batch],to=offsets[batch+1],width=to-from;
                if(width<minimumParallelWidth||workers<2){RunSegments(runRange,from,to);continue;}
                int chunks=Math.Min(workers,width);
                Parallel.For(0,chunks,options,chunk=>RunSegments(runRange,
                    from+(int)((long)width*chunk/chunks),from+(int)((long)width*(chunk+1)/chunks)));
            }
        }
        private void RunSegments(Action<int,int> runRange,int from,int to)
        {
            while(from<to)
            {
                int first=ordered[from++],end=first+1;
                while(from<to&&ordered[from]==end){from++;end++;}
                runRange(first,end);
            }
        }
        private static void Grow(ref int[] values,int size){if(values.Length<size)values=new int[Math.Max(size,values.Length*2)];}
    }
}
