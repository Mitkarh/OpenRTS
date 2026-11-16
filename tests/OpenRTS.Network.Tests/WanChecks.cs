using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;
using OpenRTS.Network;

internal static class WanChecks
{
    private static SimulationWorld World()
    {
        var world = new SimulationWorld([new UnitDefinition("unit",100)],65536,65536,42,navigationGrid:new NavigationGrid(64,64));
        world.AddInitialUnit(0,new(2048,2048),1); world.AddInitialUnit(0,new(4096,4096),2); return world;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static object Profile(int rtt, int jitter, int loss, int delay, bool slow = false, int bandwidth = 1000000)
    {
        using var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
        using var link = new WanLink(((IPEndPoint)listener.LocalEndpoint).Port,rtt,jitter,loss,bandwidth);
        var client = new TcpClient(); client.Connect(IPAddress.Loopback,link.Port);
        var host = listener.AcceptTcpClient(); listener.Stop();
        var descriptor = new MatchDescriptor("wan-test-v3","unit100","map64",42,InputDelay:delay,EndTick:100,HashInterval:8);
        using var a = new MemoryStream(); using var b = new MemoryStream();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var samples = new List<NetworkTickSample>(); var times = new List<double>();
        long hostTick=0, clientTick=0, largestLead=0;
        var clock=Stopwatch.StartNew();
        void Observe(uint player, SimulationWorld w)
        {
            if (player == 1) { Interlocked.Exchange(ref hostTick,w.Tick); times.Add(clock.Elapsed.TotalMilliseconds); }
            else { Interlocked.Exchange(ref clientTick,w.Tick); if (slow && w.Tick is >= 20 and < 30) Thread.Sleep(100); }
            long lead=Math.Abs(Interlocked.Read(ref hostTick)-Interlocked.Read(ref clientTick));
            long old; do { old=Interlocked.Read(ref largestLead); if (lead<=old) break; } while (Interlocked.CompareExchange(ref largestLead,lead,old)!=old);
        }
        IReadOnlyList<WorldCommand> Input(uint player,long tick) => tick>=20 && tick%10==0
            ? [new(0,0,0,CommandKind.Move,new(player),new(8000+(int)(tick%30)*64,(int)player*2048))] : [];
        var tasks = new[] {
            Task.Run(()=> DirectMatch.Run(host,1,descriptor,World,t=>Input(1,t),a,stop.Token,realtime:true,
                afterTick:w=>Observe(1,w),observe:s=>samples.Add(s))),
            Task.Run(()=> DirectMatch.Run(client,2,descriptor,World,t=>Input(2,t),b,stop.Token,realtime:true,
                afterTick:w=>Observe(2,w))) };
        Task.WaitAll(tasks);
        Check(tasks[0].Result==tasks[1].Result,"WAN peers differ");
        using (var reference=World())
        {
            reference.RestrictCommandsToPlayers();long[] sequences=[0,0];
            for(long tick=1;tick<=descriptor.EndTick;tick++)
            {
                var commands=new List<WorldCommand>();
                for(uint player=1;player<=2;player++)
                    foreach(var command in Input(player,tick)) commands.Add(command.WithStamp(tick,player,++sequences[player-1]));
                reference.Step(commands.ToArray());
            }
            Check(reference.Report()==tasks[0].Result,"WAN dropped or repeated the fixed input schedule");
        }
        a.Position=0; b.Position=0;
        Check(MatchJournal.Replay(a,descriptor,World)==tasks[0].Result,"WAN host replay differs");
        Check(MatchJournal.Replay(b,descriptor,World)==tasks[1].Result,"WAN join replay differs");
        Check(largestLead <= 2*delay+descriptor.HashInterval,"Slow peer caused unbounded lead");
        Check(samples.All(s=>s.SendQueueHighWater<=PeerPipeline.Capacity && s.ReceiveQueueHighWater<=PeerPipeline.Capacity),"Queue bound exceeded");
        double P(IEnumerable<double> source,double q) { var values=source.Order().ToArray(); return values.Length==0?0:values[(int)Math.Ceiling((values.Length-1)*q)]; }
        // Exclude bootstrap and terminal control exchange from the steady execution-rate measurement.
        double rate = 60*1000.0/(times[90]-times[30]);
        if (loss==0 && !slow && bandwidth>=1000000) Check(rate>=22,"Pipeline missed >=22Hz baseline for the configured delay window");
        Check(clock.Elapsed < TimeSpan.FromSeconds(25),"Profile exceeded bounded completion budget");
        return new {rtt,jitter,loss,delay,slow,bandwidth, ticks=100, steadyHz=Math.Round(rate,2),
            elapsedMs=Math.Round(clock.Elapsed.TotalMilliseconds,1),
            waitP50=P(samples.Select(s=>s.WaitMilliseconds),.5),waitP95=P(samples.Select(s=>s.WaitMilliseconds),.95),
            sealedToApplyP50=P(samples.Where(s=>s.SealedToAppliedMilliseconds.HasValue).Select(s=>s.SealedToAppliedMilliseconds!.Value),.5),
            sealedToApplyP95=P(samples.Where(s=>s.SealedToAppliedMilliseconds.HasValue).Select(s=>s.SealedToAppliedMilliseconds!.Value),.95),
            stalls=samples.Count(s=>s.WaitMilliseconds>5), maxLead=largestLead,
            sendQueueHighWater=samples.Max(s=>s.SendQueueHighWater),receiveQueueHighWater=samples.Max(s=>s.ReceiveQueueHighWater),
            segments=link.Segments,modeledRetransmissions=link.Retransmissions,bytes=link.Bytes,hash=tasks[0].Result.StateHash};
    }
    internal static void Run(Action<string,Action> test)
    {
        test("checkpoint cadence is bounded and bound into match identity", () => {
            var d=new MatchDescriptor("cadence","unit100","map64",42);
            Check(Wire.Hash(DirectMatch.Descriptor(d))!=Wire.Hash(DirectMatch.Descriptor(d with {HashInterval=16})),"Cadence did not change identity");
            foreach(int interval in new[]{0,65})
            {
                try {DirectMatch.Descriptor(d with {HashInterval=interval});throw new Exception("Expected invalid cadence");}
                catch(ArgumentException) { }
            }
        });
        test("pipeline preserves commands/replay at RTT150 with jitter and modeled loss", ()=> {
            Console.WriteLine(JsonSerializer.Serialize(Profile(150,20,5,8)));
        });
        test("slow simulation stays bounded and converges after recovery", ()=> {
            Console.WriteLine(JsonSerializer.Serialize(Profile(50,0,0,6,true)));
        });
        test("withheld checkpoints cannot advance beyond the verification window", () => {
            using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            using var fake=new TcpClient();fake.Connect(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
            var host=listener.AcceptTcpClient();listener.Stop();
            var d=new MatchDescriptor("missing-hash","unit100","map64",42,EndTick:100);
            using var journal=new MemoryStream();using var stop=new CancellationTokenSource(3000);
            SimulationWorld? world=null;
            var owner=Task.Run(()=>DirectMatch.Run(host,1,d,()=>world=World(),_=>[],journal,stop.Token,TimeSpan.FromMilliseconds(200)));
            using var stream=fake.GetStream();
            var hello=Wire.Receive(stream,stop.Token).GetAwaiter().GetResult();hello[5]=2;Array.Clear(hello,9,16);
            Wire.Send(stream,hello,stop.Token).GetAwaiter().GetResult();
            var ready=Wire.Receive(stream,stop.Token).GetAwaiter().GetResult();Wire.Send(stream,ready,stop.Token).GetAwaiter().GetResult();
            try
            {
                while(!owner.IsCompleted)
                {
                    var frame=Wire.Receive(stream,stop.Token).GetAwaiter().GetResult();
                    if(frame[0]!=3) continue;
                    var batch=Wire.ReadBatch(frame);
                    Wire.Send(stream,Wire.Batch(batch.Match,2,batch.Tick,[]),stop.Token).GetAwaiter().GetResult();
                }
            }
            catch(IOException) { }
            try {owner.GetAwaiter().GetResult();throw new Exception("Expected missing-checkpoint timeout");}
            catch(TimeoutException) {Check(world!.Tick==2*d.InputDelay+d.HashInterval,"Unverified simulation exceeded its window");}
        });
        test("buffered traffic cannot bypass an expired progress deadline", () => {
            using var stream=new MemoryStream();
            Wire.Send(stream,new byte[]{5},default).GetAwaiter().GetResult();stream.Position=0;
            using var pipeline=new PeerPipeline(stream,TimeSpan.FromSeconds(1),default);
            Check(SpinWait.SpinUntil(()=>pipeline.ReceiveHighWater>0,1000),"Reader did not buffer the frame");
            try {pipeline.Read(TimeSpan.Zero);throw new Exception("Expected expired deadline");}
            catch(TimeoutException) { }
        });
        test("read-side EOF racing the final writer continuation does not discard a flushed ACK", () => {
            using var stream=new EofDuringWriteStream();
            using var pipeline=new PeerPipeline(stream,TimeSpan.FromSeconds(1),default);
            pipeline.Send([7]);pipeline.Flush();
            Check(stream.Finished,"Final write was abandoned on read EOF");
        });
        test("a peer that stops reading times out within bounded queue capacity", ()=> {
            using var blocked = new BlockedStream();
            using var pipeline = new PeerPipeline(blocked,TimeSpan.FromMilliseconds(150),default);
            var clock=Stopwatch.StartNew();
            try { for (int i=0;i<100;i++) pipeline.Send(new byte[4096]); pipeline.Flush(); throw new Exception("Expected timeout"); }
            catch (TimeoutException) { Check(clock.Elapsed<TimeSpan.FromSeconds(3),"Slow reader timeout was not bounded"); }
            Check(pipeline.SendHighWater<=PeerPipeline.Capacity,"Slow reader grew the outgoing queue");
        });
    }
    internal static void Profiles(string output)
    {
        var results=new List<object>();
        foreach(var p in new[] {(0,0,0,2),(50,20,0,4),(150,20,1,8),(300,80,0,12),(300,80,5,12)})
        {
            var result=Profile(p.Item1,p.Item2,p.Item3,p.Item4); results.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        results.Add(Profile(50,0,0,6,true));
        results.Add(Profile(150,20,0,8,false,800));
        var hashes=results.Select(r=>JsonSerializer.SerializeToElement(r).GetProperty("hash").GetString()).Distinct().Count();
        Check(hashes==1,"Fixed input schedule diverged across WAN profiles");
        File.WriteAllText(output,JsonSerializer.Serialize(new {model="bounded TCP relay; periodic segment loss + 150ms retransmission/HOL model; not kernel netem",results},new JsonSerializerOptions{WriteIndented=true}));
    }
    private sealed class EofDuringWriteStream : Stream
    {
        private readonly TaskCompletionSource _eof=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Finished;
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){await _eof.Task.WaitAsync(ct);return 0;}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default){_eof.TrySetResult();await Task.Delay(50,ct);Finished=true;}
        public override void Flush(){} public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
    private sealed class BlockedStream : Stream
    {
        private readonly CancellationTokenSource _closed = new();
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException(); public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default) {using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_closed.Token); await Task.Delay(Timeout.Infinite,linked.Token);return 0;}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default) {using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_closed.Token);await Task.Delay(Timeout.Infinite,linked.Token);}
        protected override void Dispose(bool disposing){_closed.Cancel();base.Dispose(disposing);}
        public override void Flush(){} public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
}
