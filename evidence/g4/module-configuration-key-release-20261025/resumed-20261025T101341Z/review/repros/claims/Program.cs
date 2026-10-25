using Hexalith.Builds.Tooling.Runtime;
var root = Path.Combine(Path.GetTempPath(), "review-claim-state-" + Guid.NewGuid().ToString("N"));
var claims = new CompositionResourceClaimStore(root);
var run = CompositionRunPlanFactory.NewRunId();
claims.Acquire(run, Enumerable.Range(0, 1000).Select(i => "resource:" + i));
Console.WriteLine("receipt-bytes=" + new FileInfo(Path.Combine(root,"claims","owners",run+".json")).Length);
try { Console.WriteLine("before=" + claims.FindOwned(run).Count); } catch (Exception e) { Console.WriteLine("before-error=" + e.GetType().Name + ":" + e.Message); }
claims.ReleaseOwned(run);
try { Console.WriteLine("after=" + claims.FindOwned(run).Count); } catch (Exception e) { Console.WriteLine("after-error=" + e.GetType().Name + ":" + e.Message); }
