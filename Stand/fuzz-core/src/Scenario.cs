using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KenseiStand.FuzzCore {
    // A scenario is everything a run needs: the setup flags, the listener definitions and the
    // list of top-level operations. It is generated from a seed and stored as JSON; replay and
    // shrinking work on the JSON, so a minimised scenario no longer needs its seed.
    public sealed class Scenario {
        public int Format = 1;
        public long Seed;
        public string Layout = "narrow";
        public string[] Excluded;   // families the generator steered away from (--exclude-families)
        public bool RecTyped = true;
        public bool RecFilters = true;
        public bool RecWorld = true;
        public bool Group;          // owning group over A and B at setup
        public bool Uncapped;       // crash mode: reactions are not capped by depth or fire count
        public string Origin;       // "random", "motif:<name>", "corpus:<file>"
        public List<ListenerDef> Listeners = new();
        public List<Op> Ops = new();

        public Scenario Clone() {
            return FromJson(ToJson());
        }

        public Scenario WithOps(List<Op> ops) {
            var s = Clone();
            s.Ops = new List<Op>();
            foreach (var op in ops) {
                s.Ops.Add(op.Clone());
            }
            return s;
        }

        public ListenerDef FindListener(int lid) {
            foreach (var l in Listeners) {
                if (l.Lid == lid) {
                    return l;
                }
            }
            return null;
        }

        private static readonly JsonSerializerOptions Options = new() {
            IncludeFields = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly JsonSerializerOptions PrettyOptions = new() {
            IncludeFields = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string ToJson(bool pretty = false) => JsonSerializer.Serialize(this, pretty ? PrettyOptions : Options);

        public static Scenario FromJson(string json) => JsonSerializer.Deserialize<Scenario>(json, Options);

        public static Scenario Load(string path) => FromJson(File.ReadAllText(path));
    }

    // A listener with scripted reactions. Kind: "typed" (IComponentListener<T> on the pool of T),
    // "filter" (IFilterListener on filter F) or "world" (IWorldEventListener).
    public sealed class ListenerDef {
        public int Lid;
        public string Kind;
        public string T;
        public string F;
        public List<Rule> Rules = new();

        public ListenerDef Clone() {
            var l = (ListenerDef)MemberwiseClone();
            l.Rules = new List<Rule>();
            foreach (var r in Rules) {
                l.Rules.Add(r.Clone());
            }
            return l;
        }
    }

    // On: typed "added"/"removed"; filter "enter"/"leave"; world "created"/"destroyed"/"compAdded"/"compRemoved".
    // T: optional component kind filter for world compAdded/compRemoved. Max: fire budget per side.
    public sealed class Rule {
        public string On;
        public string T;
        public int Max = 1;
        public List<Step> Do = new();

        public Rule Clone() {
            var r = (Rule)MemberwiseClone();
            r.Do = new List<Step>();
            foreach (var s in Do) {
                r.Do.Add(s.Clone());
            }
            return r;
        }
    }

    // One nested operation of a reaction. Who: "self" (the callback's entity), "partner" (linked to self),
    // "last" (created earlier in the same reaction) or the name of a top-level entity.
    public sealed class Step {
        public string A;
        public string Who;
        public string T;
        public int Id;
        public int P;
        public int M = -1;
        public int Slot;
        public bool G;          // guard: add only if absent, remove only if present
        public string Name;     // bind the created entity to a top-level name
        public int Lid;

        public Step Clone() => (Step)MemberwiseClone();
    }

    public sealed class Op {
        public string K;        // operation kind, see Executor.Exec
        public string E;        // entity name (target / source)
        public string E2;       // second entity name (link)
        public string T;        // component kind
        public int Id;
        public int P;
        public int M = -1;      // marker written through the returned ref (add) or by a write op
        public string F;        // filter name
        public string[] Inc;
        public string[] Exc;
        public string[] Any;
        public int Lid;
        public int Slot;
        public bool Stale;      // KENSEI_DEBUG: run on a dead handle on purpose and expect a throw
        public string Name;     // name bound to the created / copied entity
        public string[] Plan;   // filter iteration: per-visit actions, cycled
        public int N1;
        public int N2;

        public Op Clone() {
            var o = (Op)MemberwiseClone();
            o.Inc = Inc == null ? null : (string[])Inc.Clone();
            o.Exc = Exc == null ? null : (string[])Exc.Clone();
            o.Any = Any == null ? null : (string[])Any.Clone();
            o.Plan = Plan == null ? null : (string[])Plan.Clone();
            return o;
        }

        public override string ToString() {
            var sb = new System.Text.StringBuilder(K);
            if (E != null) sb.Append(' ').Append(E);
            if (E2 != null) sb.Append(' ').Append(E2);
            if (T != null) sb.Append(' ').Append(T);
            if (F != null) sb.Append(" f=").Append(F);
            if (Name != null) sb.Append(" ->").Append(Name);
            if (K == "listen" || K == "unlisten") sb.Append(" L").Append(Lid);
            if (K == "group" && Inc != null) sb.Append(" <").Append(string.Join(",", Inc)).Append('>');
            if (Stale) sb.Append(" stale");
            return sb.ToString();
        }
    }

    // Result of one run (one scenario on both sides).
    public sealed class RunResult {
        public string Status;           // pass | fail | discard
        public string Sig;              // failure signature: rule@op[reaction]{facts}
        public string Family;           // defect family matched by mechanism, or NEW
        public string FindingStatus;    // defect | question | regression (see Classifier.StatusOf, Executor.Run)
        public string RegressionReason;
        public List<string> Facts;      // mechanism facts of the failing operation (up to the first difference)
        public List<string> Mirrors;    // contract questions whose mirrored behaviour came before the failure
        public List<string> Claims;     // documented promises the failure rests on
        public List<string> InactiveClaims;   // of those, the ones the tree's documents do not make yet
        public string Ctx;              // reaction in which the first difference happened ("top", "typed-removed", "state", ...)
        public int Stage;               // check that found it: 0 callback check, 1 outcome, 2 event log, 3 consistency, 4 anomaly, 5 state
        public int Pos = -1;            // log position of the first difference (stages 0 and 2)
        public int LogDiff = -1;        // stage 1: the first position where the event logs differ (-1: they agree)
        public List<string> Readings;   // alternative readings under which this failure was found (see Executor.Run)
        public List<(string question, int pos)> MirrorAt;   // mirrored questions before the failure, with their log positions
        public string Class;            // finding class (see Classifier)
        public string Rule;             // violated rule id
        public int OpIndex = -1;
        public string OpText;
        public string Detail;
        public List<string> RealLog;    // event log of the failing op (real side)
        public List<string> ModelLog;   // event log of the failing op (model)
        public List<string> Questions;  // contract questions met on the way
        public List<string> Notes;      // non-fatal observations (for example a list both owned and free)
        public int OpsRun;
        public long Events;
    }
}
