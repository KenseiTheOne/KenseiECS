using System;
using System.Collections.Generic;

namespace Stand.Contracts {
    /// <summary> Verdict of one matrix cell or check, from harmless to defect. </summary>
    public enum Verdict {
        /// <summary> Returned normally, or threw an exception of an expected kind (ODE, IOE, ArgumentException with a bad argument...). </summary>
        Ok = 0,
        /// <summary> Suspicious on its face (NRE, IndexOutOfRange, a changed state...), but the docs of this version say so. </summary>
        Doc = 1,
        /// <summary> The docs call this input undefined behaviour (release with a stale handle): nothing is promised. </summary>
        Undefined = 2,
        /// <summary> Suspicious, and no document of this version promises otherwise: a question to the contract, not a defect. </summary>
        Question = 3,
        /// <summary> Contradicts a promise written in the documentation of this version. </summary>
        Finding = 4,
        /// <summary> The member could not be called (by-ref-like parameter, generic it cannot close...). </summary>
        NotRun = 5
    }

    /// <summary> What a call did: returned (with a printable value) or threw. </summary>
    public sealed class Outcome {
        public bool Threw;
        public string ExType;        // full name
        public string ExShort;       // short name
        public string Message;
        public string ParamName;     // ArgumentException family
        public string Site;          // first KenseiECS frame: Type.Method
        public string TopFrame;      // first frame of any kind
        public List<string> Frames = new();  // KenseiECS frames, innermost first
        public string Value;         // printable result when it returned
        public bool AnyNullArg;      // some argument (or the target) was null

        public string Class {
            get {
                if (!Threw) {
                    return "ok";
                }
                return ClassOf(ExType, AnyNullArg);
            }
        }

        public static string ClassOf(string exType, bool anyNullArg) {
            switch (exType) {
                case "System.ObjectDisposedException": return "ODE";
                case "System.ArgumentNullException": return anyNullArg ? "ArgNull" : "ArgNull(no-null)";
                case "System.ArgumentOutOfRangeException": return "ArgRange";
                case "System.ArgumentException": return "Arg";
                case "System.InvalidOperationException": return "IOE";
                case "System.Collections.Generic.KeyNotFoundException": return "KeyNotFound";
                case "System.NullReferenceException": return "NRE";
                case "System.IndexOutOfRangeException": return "IndexOOR";
                case "System.InvalidCastException": return "Cast";
                case "System.OutOfMemoryException": return "OOM";
                case "System.IO.InvalidDataException": return "InvalidData";
                case "System.IO.EndOfStreamException": return "EndOfStream";
                case "System.NotSupportedException": return "NotSupported";
                case "System.AggregateException": return "Aggregate";
                case "CRASH": return "CRASH";
                case "HANG": return "HANG";
                default: return "Other:" + Short(exType);
            }
        }

        public static string Short(string fullName) {
            if (fullName == null) {
                return null;
            }
            int dot = fullName.LastIndexOf('.');
            return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
        }

        /// <summary> NRE, IndexOutOfRange, ArgumentNull without a null argument, cast, crash and any type the package does not throw on purpose. </summary>
        public bool Suspicious {
            get {
                if (!Threw) {
                    return false;
                }
                var c = Class;
                return c == "NRE" || c == "IndexOOR" || c == "ArgNull(no-null)" || c == "Cast" || c == "CRASH" || c == "HANG"
                    || c == "OOM" || c.StartsWith("Other:", StringComparison.Ordinal);
            }
        }

        public string Signature => Threw ? "ex:" + ExShort : "ok:" + (Value ?? "void");
    }

    /// <summary> One observed probe value after a call, compared with the same probe on a fresh state. </summary>
    public sealed class ProbeDiff {
        public string Probe;
        public string Expected;   // on a fresh object in the same state
        public string Actual;     // after the call
        public bool Extended;     // not one of the safe queries the stand promises (pool/filter creation)
    }

    /// <summary> One rule's opinion about a case. A case can collect several (documented NRE + a changed state). </summary>
    public sealed class Judgement {
        public Verdict Verdict;
        public string Rule;       // contract, xml-exception, state, argnull, suspicious, ...
        public string Severity;   // high | medium | low | info
        public string Reason;     // for the report
        public string DocRef;     // where the contract is written
        public string Quote;      // Doc: the sentence of the tree's documents it rests on, as found there
        public string ClaimId;
        public string ClaimState; // active | profile | later | missing: how the claim's wording relates to this version's docs
        public string Since;      // the version whose documentation makes the promise
        public List<string> Damage;  // state rules: "probe=change" of the diffs the judgement is about
    }

    /// <summary> One executed matrix case. </summary>
    public sealed class CaseResult {
        public int Ordinal;
        public string Key;
        public string Target;
        public string Member;      // display signature
        public string Name;        // simple member name (property name for a getter)
        public string MemberId;    // XML doc id
        public string Scenario;
        public string Variant;
        public string CallCode;    // C# of the call
        public List<string> ArgCodes = new();
        public Outcome Outcome;
        public List<ProbeDiff> Diffs;
        public List<ProbeDiff> SmokeDiffs;          // ordinary use after a call that accepted null
        public List<string> UnknownParams = new();  // parameters the factory passed null for, not knowing the type
        public List<Judgement> Judgements = new();
        public Verdict Verdict;
        public string WhyNotRun;
        public string Unstable;                     // --full: the outcome under the second case order, when it differs
        public double Ms;
        // Claims that apply to the case but whose wording these docs lack although the version should have it (state
        // "missing"): a verdict that changed only because of them is the stand's (a claim warning), not a regression.
        public List<string> MissingClaims = new();

        public Judgement Worst {
            get {
                Judgement worst = null;
                foreach (var j in Judgements) {
                    if (worst == null || Rank(j) > Rank(worst)) {
                        worst = j;
                    }
                }
                return worst;
            }
        }

        public static int Rank(Judgement j) {
            int v = (int)j.Verdict * 10;
            switch (j.Severity) {
                case "high": return v + 3;
                case "medium": return v + 2;
                case "low": return v + 1;
                default: return v;
            }
        }
    }

    /// <summary>
    /// A defect, contract question, documented oddity, regression or diagnostics note, as written to
    /// report.json. Signature is the mechanism and the place (rule, claim, throw site, kind of damage),
    /// stable between runs and versions; Hits is how many matrix cases (or checks) fall into it.
    /// </summary>
    public sealed class Finding {
        public string Id;
        public string Known;       // id of a known problem this one matches (G1, G2, ...), see Known.cs
        public string Kind;        // defect | question | documented | regression | diagnostic
        public string Class;       // state-corruption, contract-violation, misleading-exception, diagnostics, leak, ...
        public string Rule;
        public string Severity;
        public string Title;
        public string Detail;
        public string Member;
        public string Scenario;
        public string Variant;
        public string Outcome;
        public string Expected;
        public string DocRef;
        public string Quote;       // documented: the sentence of the tree's documents it rests on, as written there
        public string ClaimState;  // active | profile | later | missing (claims only)
        public string Since;       // the version that makes the promise (claims only)
        public string Signature;
        public int Hits;
        public string Repro;       // path relative to the report folder
        public string ReproStatus; // verified | mismatch | error | compile-error | not-run
        public string ReproObserved;
        public int Seed;
        public string Params;      // flavor, cfg, mode, case key
        public List<string> AlsoVia = new();
        public List<string> CaseKeys = new();
    }
}
