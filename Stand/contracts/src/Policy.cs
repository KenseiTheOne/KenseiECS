using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KenseiECS;
using Stand.Contracts.Probe;

namespace Stand.Contracts {
    /// <summary> Everything a rule may look at for one executed case. </summary>
    public sealed class CaseCtx {
        public Member Member;
        public string Scenario;
        public Variant Variant;
        public string Curated;           // label of a curated case, or null
        public bool Debug;
        public ArgValue[] Args;
        public Outcome Outcome;
        public object Result;
        public List<ProbeDiff> Diffs;    // after the call vs a fresh instance of the same state
        public List<ProbeDiff> SmokeDiffs;   // ordinary use after a call that accepted null, vs a fresh state
        public Ctx Ctx;                  // after the call
        public Docs Docs;

        public string Name => Member.Name;
        public Type TargetType => Member.Target.Type;
        public bool IsWorld => TargetType == typeof(World);
        public bool Threw => Outcome.Threw;
        public bool LiveWorld => Scenario != Scenarios.WorldDestroyed;
        public string Kind => Variant?.Kind;

        /// <summary> The scenario's world exists, was not destroyed by the scenario, and the call did not destroy it. </summary>
        public bool WorldStillLive => LiveWorld && Ctx?.World != null && !Ctx.World.IsDestroyed;

        public bool VariedType(Type t) {
            if (Variant == null || Variant.Slot < 0) {
                return false;
            }
            var pt = Member.Params[Variant.Slot].ParameterType;
            return (pt.IsByRef ? pt.GetElementType() : pt) == t;
        }

        /// <summary> A dead handle reaches the call: a stale, null or default Entity, or any handle of a destroyed world. </summary>
        public bool DeadEntityArg {
            get {
                if ((Kind == "stale" || Kind == "null-entity" || Kind == "default") && VariedType(typeof(Entity))) {
                    return true;
                }
                return Scenario == Scenarios.WorldDestroyed && Member.Params.Any(p => p.ParameterType == typeof(Entity));
            }
        }

        public bool PassesThrough(params string[] methods) {
            foreach (var f in Outcome.Frames) {
                foreach (var m in methods) {
                    if (f == m) {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary> Result of checking one claim on one case. </summary>
    public sealed class ClaimCheck {
        public bool Violated;
        public string Reason;
        public Verdict OnOk = Verdict.Ok;   // Doc or Undefined when the claim explains a suspicious outcome
        public string Severity = "medium";

        public static ClaimCheck Ok(Verdict v = Verdict.Ok, string reason = null) => new ClaimCheck { OnOk = v, Reason = reason };
        public static ClaimCheck Bad(string reason, string severity = "medium") => new ClaimCheck { Violated = true, Reason = reason, Severity = severity };
    }

    public enum ClaimKind {
        /// <summary> About the call itself: its exception, its result, its effect. </summary>
        Call,
        /// <summary> About the safe queries after the call (the probes): which of them must still answer as on a fresh object. </summary>
        State,
        /// <summary> Work that stays done when the member throws: a changed state after its exception is documented. </summary>
        Partial
    }

    /// <summary>
    /// A documented promise. It is active only when its wording exists in the documentation of the
    /// tree under test; Since is the version whose documentation first makes it (see VersionProfile).
    /// </summary>
    public sealed class Claim {
        public string Id;
        public ClaimKind Kind = ClaimKind.Call;
        public string Since = "2.1.1";
        public string Quote;          // the promise, as written
        public string[] Phrases;      // any of them activates the claim
        public bool DebugCheck;       // the claim documents a KENSEI_DEBUG exception
        public Func<CaseCtx, bool> Applies;
        public Func<CaseCtx, ClaimCheck> Check;                                  // Call
        public Func<CaseCtx, List<ProbeDiff>, List<ProbeDiff>> Violations;       // State: the diffs that break it
        public bool CoversInvariant;                                             // Partial: also an entity left without components
        // A claim whose row or sentence a later version replaced with another claim's: where the successor's wording is in
        // the docs and this one's is not, it is "superseded" (no stand warning, nothing to judge: the successor judges).
        public string SupersededBy;
    }

    /// <summary> How a claim's wording relates to the documentation of this version. </summary>
    public sealed class ClaimActivation {
        public string State;    // active | profile | later | missing | superseded
        public string Where;    // the page, xml:<id> or CHANGELOG with the wording; for profile, the reason
        public string Phrase;   // active: the wording found there
        public bool Holds => State == "active" || State == "profile";
    }

    /// <summary>
    /// The policy of the stand: a defect is only a contradiction of a promise written in the
    /// documentation of the version under test (a curated claim, an XML &lt;exception&gt; tag). A
    /// suspicious outcome no document of this version speaks to — an NRE, ArgumentNullException without a
    /// null argument, a state that answers differently after a failed call, a promise made only by a
    /// later version — is a question to the contract. Documented exceptions and documented partial
    /// effects pass (Doc).
    /// </summary>
    public sealed class Policy {
        private readonly Docs _docs;
        private readonly bool _debug;
        private readonly VersionProfile _profile;
        private readonly List<Claim> _claims;
        private readonly Dictionary<string, ClaimActivation> _activation = new();

        public Policy(Docs docs, bool debug, VersionProfile profile) {
            _docs = docs;
            _debug = debug;
            _profile = profile ?? new VersionProfile();
            _claims = BuildClaims();
            foreach (var c in _claims) {
                _activation[c.Id] = Activate(c.Id, c.Since, c.Phrases);
            }
            foreach (var c in _claims.Where(x => x.SupersededBy != null)) {
                var a = _activation[c.Id];
                if (a.State == "missing" && _activation.TryGetValue(c.SupersededBy, out var next) && next.Holds) {
                    _activation[c.Id] = new ClaimActivation { State = "superseded", Where = "replaced by " + c.SupersededBy + " in these docs (" + next.Where + ")" };
                }
            }
        }

        public VersionProfile Profile => _profile;

        public IEnumerable<(Claim Claim, ClaimActivation Activation)> Claims => _claims.Select(c => (c, _activation[c.Id]));

        /// <summary>
        /// Where the wording of a promise is, for this tree. Claims with an id starting "xml/" are looked
        /// up in the XML documentation first. Without any docs/ page, a docs-page promise counts as made
        /// by every version from since on (the profile).
        /// </summary>
        public ClaimActivation Activate(string id, string since, params string[] phrases) {
            foreach (var p in phrases) {
                var at = _docs.Find(p, id.StartsWith("xml/", StringComparison.Ordinal));
                if (at != null) {
                    return new ClaimActivation { State = "active", Where = at, Phrase = p };
                }
            }
            if (!_profile.Known) {
                return new ClaimActivation { State = "missing", Where = "wording not found; package version unknown" };
            }
            if (!_profile.AtLeast(since)) {
                return new ClaimActivation { State = "later", Where = "promised from " + since + " on; not in the docs of " + _profile };
            }
            if (!_docs.HasPages && !id.StartsWith("xml/", StringComparison.Ordinal)) {
                return new ClaimActivation { State = "profile", Where = "version profile " + _profile + " (docs/ of this tree not found; the docs of " + since + " make the promise)" };
            }
            return new ClaimActivation { State = "missing", Where = "wording not found although " + _profile + " >= " + since + ": the docs were rewritten, update the claim" };
        }

        public ClaimActivation ActivationOf(string id) => _activation.TryGetValue(id, out var a) ? a : null;

        public void Judge(CaseCtx cc, CaseResult r) {
            var o = cc.Outcome;
            bool explained = false;      // a claim or tag accepts the outcome
            bool undefined = false;
            Claim debugCheck = null;     // the exception is a documented KENSEI_DEBUG check

            foreach (var claim in _claims.Where(c => c.Kind == ClaimKind.Call)) {
                var act = _activation[claim.Id];
                bool applies;
                try {
                    applies = claim.Applies(cc);
                } catch (Exception) {
                    applies = false;
                }
                if (!applies) {
                    continue;
                }
                if (act.State == "superseded") {
                    // The docs of this version replaced the promise; its successor judges the case.
                    continue;
                }
                var check = claim.Check(cc);
                if (act.State == "missing" && !r.MissingClaims.Contains(claim.Id)) {
                    r.MissingClaims.Add(claim.Id);
                }
                if (!act.Holds) {
                    // A promise this version's documentation does not make: a violation is a question.
                    if (check.Violated) {
                        r.Judgements.Add(new Judgement {
                            Verdict = Verdict.Question, Rule = "contract-" + act.State, ClaimId = claim.Id, ClaimState = act.State, Since = claim.Since,
                            Severity = check.Severity, Reason = check.Reason + " (" + act.Where + ")", DocRef = "not in these docs: " + claim.Quote
                        });
                    }
                    continue;
                }
                if (check.Violated) {
                    r.Judgements.Add(new Judgement {
                        Verdict = Verdict.Finding, Rule = "contract", ClaimId = claim.Id, ClaimState = act.State, Since = claim.Since, Severity = check.Severity,
                        Reason = check.Reason, DocRef = act.Where + ": " + claim.Quote
                    });
                    continue;
                }
                explained = true;
                if (claim.DebugCheck && _debug && cc.Threw) {
                    debugCheck = claim;
                }
                Verdict v;
                if (check.OnOk == Verdict.Undefined) {
                    undefined = true;
                    v = Verdict.Undefined;
                } else if (o.Suspicious) {
                    v = Verdict.Doc;     // the claim explains an outcome that looks wrong on its face
                } else {
                    v = check.OnOk;
                }
                r.Judgements.Add(new Judgement {
                    Verdict = v, Rule = "contract", ClaimId = claim.Id, ClaimState = act.State, Since = claim.Since, Severity = "info",
                    Reason = check.Reason ?? "as documented", DocRef = act.Where + ": " + claim.Quote
                });
            }

            JudgeXmlExceptions(cc, r, ref explained);

            if (!undefined && cc.Diffs != null) {
                JudgeState(cc, r, debugCheck);
            }

            // ArgumentNullException although no argument was null: the exception names a parameter the caller
            // never passed. A .NET convention, not a promise of the package: a question.
            if (o.Threw && o.ExType == "System.ArgumentNullException" && !o.AnyNullArg) {
                r.Judgements.Add(new Judgement {
                    Verdict = Verdict.Question, Rule = "argnull", Severity = "medium",
                    Reason = "ArgumentNullException(" + (o.ParamName ?? "?") + ") without a null argument: the exception names a parameter the caller did not pass (" +
                        (cc.Member.Params.Length == 0 ? "the method has no parameters" : "parameters: " + string.Join(", ", cc.Member.Params.Select(p => p.Name))) + "); thrown by " + (o.TopFrame ?? "?")
                });
            }

            // A null that was accepted without an exception: questions, since no document of the package
            // says whether null is allowed there.
            if (!o.Threw && cc.SmokeDiffs != null && cc.SmokeDiffs.Count > 0) {
                r.Judgements.Add(new Judgement {
                    Verdict = Verdict.Question, Rule = "null-deferred", Severity = "low",
                    Reason = "null accepted without an exception, and a later ordinary operation fails (after the same call with a valid argument it passes): " + DiffText(cc.SmokeDiffs)
                });
            }
            if (!o.Threw && NullRegistered(cc, out string registered)) {
                r.Judgements.Add(new Judgement {
                    Verdict = Verdict.Question, Rule = "null-registered", Severity = "low",
                    Reason = "the call accepted null and registered it without an exception: " + registered
                });
            }

            if (o.Threw && o.Suspicious && !explained) {
                if (o.Class == "OOM" && cc.Scenario == Scenarios.Huge) {
                    r.Judgements.Add(new Judgement { Verdict = Verdict.Ok, Rule = "resource", Severity = "info", Reason = "OutOfMemoryException on an int.MaxValue request: a resource limit" });
                } else if (!r.Judgements.Any(j => j.Rule == "argnull")) {
                    r.Judgements.Add(new Judgement {
                        Verdict = Verdict.Question, Rule = "suspicious", Severity = "low",
                        Reason = o.ExShort + " that no document of this version describes" + (o.Site != null ? " (thrown by " + o.Site + ")" : "")
                    });
                }
            }

            if (r.Judgements.Count == 0) {
                r.Judgements.Add(new Judgement { Verdict = Verdict.Ok, Rule = o.Threw ? "expected-exception" : "returned", Severity = "info" });
            }
            r.Verdict = r.Judgements.Max(j => j.Verdict);
        }

        // =====================================================================
        // The state after the call
        // =====================================================================

        /// <summary>
        /// The safe queries after the call (Probes) against a fresh object in the same state. A changed
        /// answer is a defect only where a document of this version promises it (a State claim: the
        /// queries on a destroyed world, TryGetEntity safe for any index, DestroyEntity of a dead entity a
        /// no-op, an entity alive only with a component). After a failure, any other change is a question,
        /// or documented when the member's partial effect is (Partial claims). After a normal return the
        /// change is the call's own work, except for a pure query, where it is a question.
        /// </summary>
        private void JudgeState(CaseCtx cc, CaseResult r, Claim debugCheck) {
            var o = cc.Outcome;
            var safe = cc.Diffs.Where(d => !d.Extended).ToList();
            var covered = new HashSet<ProbeDiff>();
            Claim partial = null;
            ClaimActivation partialAct = null;
            if (o.Threw) {
                foreach (var p in _claims.Where(c => c.Kind == ClaimKind.Partial)) {
                    bool applies;
                    try {
                        applies = p.Applies(cc);
                    } catch (Exception) {
                        applies = false;
                    }
                    if (applies && _activation[p.Id].Holds) {
                        partial = p;
                        partialAct = _activation[p.Id];
                        break;
                    }
                }
            }

            foreach (var sc in _claims.Where(c => c.Kind == ClaimKind.State)) {
                bool applies;
                try {
                    applies = sc.Applies(cc);
                } catch (Exception) {
                    applies = false;
                }
                if (!applies) {
                    continue;
                }
                var bad = sc.Violations(cc, safe);
                if (bad.Count == 0) {
                    continue;
                }
                if (partial != null && partial.CoversInvariant && sc.Id == "docs/alive-iff-component") {
                    // An entity left without components is part of the documented partial effect
                    // ("a partially restored world"): judged with the rest of the changed state below.
                    continue;
                }
                covered.UnionWith(bad);
                var act = _activation[sc.Id];
                if (act.State == "missing" && !r.MissingClaims.Contains(sc.Id)) {
                    r.MissingClaims.Add(sc.Id);
                }
                bool broken = bad.Any(d => d.Expected.StartsWith("ok:", StringComparison.Ordinal) && d.Actual.StartsWith("ex:", StringComparison.Ordinal));
                string what = (o.Threw ? "after " + o.ExShort + " " : "after the call returned, ")
                    + (act.Holds ? "the safe queries break a documented promise: " : "the safe queries break a promise these docs do not make: ") + DiffText(bad);
                if (act.Holds) {
                    r.Judgements.Add(new Judgement {
                        Verdict = Verdict.Finding, Rule = "state", ClaimId = sc.Id, ClaimState = act.State, Since = sc.Since, Severity = broken ? "high" : "medium",
                        Reason = what, DocRef = act.Where + ": " + sc.Quote, Damage = Signatures.Damage(bad)
                    });
                } else {
                    r.Judgements.Add(new Judgement {
                        Verdict = Verdict.Question, Rule = "state", ClaimId = sc.Id, ClaimState = act.State, Since = sc.Since, Severity = broken ? "high" : "medium",
                        Reason = what + " (" + act.Where + ")", DocRef = "not in these docs: " + sc.Quote, Damage = Signatures.Damage(bad)
                    });
                }
            }

            var rest = safe.Where(d => !covered.Contains(d)).ToList();
            var extended = cc.Diffs.Where(d => d.Extended).ToList();
            if (o.Threw) {
                if (rest.Count > 0) {
                    bool broken = rest.Any(d => d.Expected.StartsWith("ok:", StringComparison.Ordinal) && d.Actual.StartsWith("ex:", StringComparison.Ordinal));
                    // A documented partial effect is work that stays done (commands already played back,
                    // systems that already ran). It never covers a safe query that throws afterwards.
                    if (partial != null && !broken) {
                        r.Judgements.Add(new Judgement {
                            Verdict = Verdict.Doc, Rule = "state-partial", ClaimId = partial.Id, ClaimState = partialAct.State, Since = partial.Since, Severity = "info",
                            Reason = "after the exception the state differs from a fresh object, as the documented partial effect of this member says: " + DiffText(rest),
                            DocRef = partialAct.Where + ": " + partial.Quote, Quote = partialAct.Phrase, Damage = Signatures.Damage(rest)
                        });
                    } else {
                        string severity = broken ? "high" : rest.Any(d => Probes.IsWorldProbe(d.Probe)) ? "medium" : "low";
                        r.Judgements.Add(new Judgement {
                            Verdict = Verdict.Question, Rule = "state", Severity = severity,
                            Reason = "after " + o.ExShort + (debugCheck != null ? " (the documented KENSEI_DEBUG check " + debugCheck.Id + ")" : "")
                                + " the safe queries answer differently from a fresh object in the same state, and no document of this version promises the state after this failure: " + DiffText(rest)
                                + (partial != null ? " (the member's partial effect is documented, but it does not cover a query that throws now)" : "")
                                + (extended.Count > 0 ? "; extended probes: " + DiffText(extended) : ""),
                            Damage = Signatures.Damage(rest)
                        });
                    }
                } else if (extended.Count > 0 && covered.Count == 0) {
                    r.Judgements.Add(new Judgement {
                        Verdict = Verdict.Question, Rule = "state-extended", Severity = "low",
                        Reason = "after the exception only an extended probe (creating a pool or a filter) behaves differently: " + DiffText(extended),
                        Damage = Signatures.Damage(extended)
                    });
                }
            } else if (rest.Count > 0 && IsQuery(cc.Member)) {
                r.Judgements.Add(new Judgement {
                    Verdict = Verdict.Question, Rule = "state-after-query", Severity = "medium",
                    Reason = "a query returned normally, but the safe queries after it answer differently from a fresh object: " + DiffText(rest),
                    Damage = Signatures.Damage(rest)
                });
            }
        }

        private static readonly Regex QueryName = new Regex("^(Is|Has|TryGet|Contains|IndexOf|First|Single|ToString|GetEnumerator|GetEntity|GetComponentCount|GetComponentTypes|GetSingleton|GetRunner|GetSystemInfo|GetPool|GetRaw|GetEventsByType|Equals)", RegexOptions.Compiled);

        /// <summary> A member that only reads: a property getter, Is*/Has*/TryGet*/IndexOf/Get(entity)... </summary>
        public static bool IsQuery(Member m) {
            if (m.IsCtor) {
                return false;
            }
            if (m.IsGetter) {
                return true;
            }
            return m.Name == "Get" || QueryName.IsMatch(m.Name);
        }

        // <exception cref="X">condition</exception> of the member: X is documented; when the condition names
        // the scenario (destroyed world, negative argument, null), X is also required.
        private void JudgeXmlExceptions(CaseCtx cc, CaseResult r, ref bool explained) {
            var entry = _docs.For(cc.Member.DocId);
            if (entry == null || entry.Exceptions.Count == 0) {
                return;
            }
            var o = cc.Outcome;
            bool anyMatchesType = false;
            var required = new List<(string Cref, string Cond)>();
            foreach (var (cref, cond) in entry.Exceptions) {
                if (o.Threw && IsOrDerives(o.ExType, cref)) {
                    anyMatchesType = true;
                }
                string c = cond.ToLowerInvariant();
                // A condition that names parameters ("When lists is negative") binds only those; one that
                // names none ("When an argument is negative") binds every parameter of the member.
                var named = cc.Member.Params.Where(p => p.Name != null && Regex.IsMatch(cond, @"\b" + Regex.Escape(p.Name) + @"\b")).Select(p => p.Name).ToList();
                bool varied = named.Count == 0 || (cc.Variant != null && cc.Variant.Slot >= 0 && cc.Variant.Slot < cc.Member.Params.Length
                    && named.Contains(cc.Member.Params[cc.Variant.Slot].Name));
                bool scenarioNamed =
                    (c.Contains("destroyed") && cc.Scenario == Scenarios.WorldDestroyed) ||
                    (c.Contains("negative") && (cc.Kind == "neg" || cc.Kind == "min") && cc.LiveWorld && varied) ||
                    (c.Contains("null") && cc.Kind == "null" && cc.LiveWorld && varied);
                if (scenarioNamed) {
                    required.Add((cref, cond));
                }
            }
            if (anyMatchesType) {
                explained = true;
                r.Judgements.Add(new Judgement {
                    Verdict = o.Suspicious ? Verdict.Doc : Verdict.Ok, Rule = "xml-exception", Severity = "info",
                    Reason = "the exception is documented by an <exception> tag", DocRef = "xml:" + cc.Member.DocId
                });
                return;
            }
            if (required.Count > 0) {
                var (cref, cond) = required[0];
                r.Judgements.Add(new Judgement {
                    Verdict = Verdict.Finding, Rule = "xml-exception", Severity = "medium",
                    Reason = "the XML docs promise " + Outcome.Short(cref) + " (" + cond + "), but the call " + (o.Threw ? "threw " + o.ExShort : "returned " + o.Value),
                    DocRef = "xml:" + cc.Member.DocId + ": <exception cref=\"" + cref + "\">" + cond + "</exception>"
                });
            }
        }

        private static bool IsOrDerives(string exType, string cref) {
            var t = Type.GetType(exType) ?? typeof(int).Assembly.GetType(exType);
            var c = Type.GetType(cref) ?? typeof(int).Assembly.GetType(cref) ?? typeof(System.Collections.Generic.KeyNotFoundException).Assembly.GetType(cref);
            if (t == null || c == null) {
                return exType == cref;
            }
            return c.IsAssignableFrom(t);
        }

        // A null passed for an object parameter (not a name) and a count that grew: the null itself was stored.
        private static bool NullRegistered(CaseCtx cc, out string what) {
            what = null;
            if (cc.Args == null || cc.Diffs == null) {
                return false;
            }
            // Only where the stand passed the null on purpose, not a null for a type the factory does not know.
            if (cc.Kind != "null" && (cc.Curated == null || !cc.Curated.StartsWith("null@", StringComparison.Ordinal))) {
                return false;
            }
            var nulls = new List<string>();
            for (int i = 0; i < cc.Args.Length && i < cc.Member.Params.Length; i++) {
                var a = cc.Args[i];
                var pt = cc.Member.Params[i].ParameterType;
                if (a.Value == null && a.IsNull && !a.IsOut && !pt.IsByRef && !pt.IsValueType && pt != typeof(string) && !typeof(Delegate).IsAssignableFrom(pt)) {
                    nulls.Add(cc.Member.Params[i].Name);
                }
            }
            if (nulls.Count == 0) {
                return false;
            }
            // Registries only: an entity created around the null (CreateWithListener) is the call's own work.
            var registries = new[] { "runner.SystemCount", "listeners.Count", "buffer.Count", "events.Count" };
            var grown = cc.Diffs.Where(d => !d.Extended && registries.Contains(d.Probe)
                && int.TryParse(d.Expected.Replace("ok:", ""), out int before) && int.TryParse(d.Actual.Replace("ok:", ""), out int after) && after > before).ToList();
            if (grown.Count == 0) {
                return false;
            }
            what = string.Join(", ", nulls) + " = null; " + string.Join("; ", grown.Select(d => d.Probe + ": " + d.Expected.Replace("ok:", "") + " -> " + d.Actual.Replace("ok:", "")));
            return true;
        }

        /// <summary>
        /// A case whose worker process died (CRASH) or stopped answering (HANG). No document promises
        /// anything about it, so it is a question (high severity), unless the docs call the input
        /// undefined; a request for int.MaxValue items may simply run into the machine's limits.
        /// </summary>
        public void JudgeCrash(CaseSpec spec, CaseResult r) {
            var cc = new CaseCtx {
                Member = spec.Member, Scenario = spec.Scenario, Variant = spec.Variant, Curated = spec.Curated, Debug = _debug,
                Outcome = r.Outcome, Docs = _docs, Args = null, Ctx = null, Diffs = null
            };
            string undefinedBy = null;
            foreach (var claim in _claims.Where(c => c.Kind == ClaimKind.Call)) {
                var act = _activation[claim.Id];
                if (!act.Holds) {
                    continue;
                }
                try {
                    if (claim.Applies(cc) && claim.Check(cc) is ClaimCheck check && !check.Violated && check.OnOk == Verdict.Undefined) {
                        undefinedBy = act.Where + ": " + claim.Quote;
                        break;
                    }
                } catch (Exception) {
                    // the claim needs the state after the call, which a crashed case does not have
                }
            }
            string what = r.Outcome.ExType == "HANG" ? "the call did not return in time" : "the call took the worker process down";
            if (undefinedBy != null) {
                r.Judgements.Add(new Judgement { Verdict = Verdict.Undefined, Rule = "crash-undefined", Severity = "info", Reason = what + "; the docs call this input undefined", DocRef = undefinedBy });
            } else if (spec.Scenario == Scenarios.Huge) {
                r.Judgements.Add(new Judgement { Verdict = Verdict.Question, Rule = "resource", Severity = "low", Reason = what + " on an int.MaxValue request: probably the machine's resource limit" });
            } else {
                r.Judgements.Add(new Judgement { Verdict = Verdict.Question, Rule = r.Outcome.ExType == "HANG" ? "hang" : "crash", Severity = "high", Reason = what + "; no document promises anything about it" });
            }
            r.Verdict = r.Judgements.Max(j => j.Verdict);
        }

        public static string DiffText(IEnumerable<ProbeDiff> diffs) {
            return string.Join("; ", diffs.Select(d => d.Probe + ": expected " + d.Expected + ", got " + d.Actual));
        }

        // =====================================================================
        // Curated claims: each quotes the documentation it holds the code to
        // =====================================================================

        private static bool IsProbe(ProbeDiff d, params string[] prefixes) => prefixes.Any(p => d.Probe.StartsWith(p, StringComparison.Ordinal));

        private List<Claim> BuildClaims() {
            bool dbg = _debug;
            var list = new List<Claim>();

            ClaimCheck MustThrowInDebug(CaseCtx cc, Verdict releaseVerdict, string releaseNote) {
                if (dbg) {
                    if (!cc.Threw) {
                        return ClaimCheck.Bad("under KENSEI_DEBUG the docs promise an exception, but the call returned " + cc.Outcome.Value);
                    }
                    return ClaimCheck.Ok(cc.Outcome.Suspicious ? Verdict.Doc : Verdict.Ok,
                        cc.Outcome.Suspicious ? "throws as promised, but " + cc.Outcome.ExShort + " rather than a diagnostic exception" : null);
                }
                return ClaimCheck.Ok(releaseVerdict, releaseNote);
            }

            ClaimCheck NoThrowNoChange(CaseCtx cc, string what) {
                if (cc.Threw) {
                    return ClaimCheck.Bad(what + ": the docs promise a no-op, but the call threw " + cc.Outcome.ExShort);
                }
                if (cc.Diffs != null && cc.Diffs.Count > 0) {
                    return ClaimCheck.Bad(what + ": the docs promise a no-op, but the state changed: " + DiffText(cc.Diffs));
                }
                return ClaimCheck.Ok();
            }

            // --- docs/guides/debug-mode.md, the release vs KENSEI_DEBUG table ---
            list.Add(new Claim {
                Id = "debug-table/stale-entity", DebugCheck = true,
                Quote = "`Add`/`Get`/`Has`/`Remove` with a dead or stale `Entity` | undefined (may corrupt another entity) | throws",
                // 2.2.2 extends the row ("`Add`/`Get`/`Has`/`Remove`, `AddEvent`, `Subscribe`/`Unsubscribe` with a dead or stale
                // `Entity` | undefined (may corrupt another entity) | throws, also while another entity is being destroyed; ..."):
                // the second phrase is the part every version from 2.1.1 on shares.
                Phrases = new[] { "Add/Get/Has/Remove with a dead or stale Entity | undefined (may corrupt another entity) | throws",
                                  "with a dead or stale Entity | undefined (may corrupt another entity) | throws" },
                Applies = cc => cc.IsWorld && !cc.Member.IsExtension && (cc.Name == "Add" || cc.Name == "Get" || cc.Name == "Has" || cc.Name == "Remove") && cc.DeadEntityArg,
                Check = cc => MustThrowInDebug(cc, Verdict.Undefined, "release: undefined for a dead handle")
            });
            list.Add(new Claim {
                Id = "debug-table/pool-add-dead-slot", DebugCheck = true,
                Quote = "Pool `Add(int)` on a dead slot | undefined | throws",
                Phrases = new[] { "Pool Add(int) on a dead slot | undefined | throws" },
                // Only a slot that is dead now: in stale-reused the slot holds the next entity again, and
                // the int API has no generation to tell the two apart.
                Applies = cc => cc.TargetType == typeof(ComponentPool<CA>) && cc.Name == "Add"
                    && ((cc.Kind == "stale-index" && cc.Scenario == Scenarios.StaleDead) || cc.Scenario == Scenarios.WorldDestroyed),
                Check = cc => MustThrowInDebug(cc, Verdict.Undefined, "release: undefined for a dead slot")
            });
            list.Add(new Claim {
                Id = "debug-table/pool-get-missing", DebugCheck = true, SupersededBy = "debug-table/get-missing",
                Quote = "Pool `Get(int)` without the component | reads garbage | throws",
                Phrases = new[] { "Pool Get(int) without the component | reads garbage | throws" },
                Applies = cc => cc.TargetType == typeof(ComponentPool<CA>) && cc.Name == "Get"
                    && (cc.Kind == "stale-index" || cc.Kind == "neg" || cc.Kind == "min" || cc.Kind == "huge" || cc.Scenario == Scenarios.WorldDestroyed),
                Check = cc => MustThrowInDebug(cc, Verdict.Undefined, "release: reads garbage (undefined)")
            });
            // 2.2.2 rewrote the row (CHANGELOG 2.2.2, Documentation: "Get of a missing component throws IndexOutOfRangeException
            // on Mono and .NET and reads (or through the ref writes) outside the pool's arrays on IL2CPP") and made it cover
            // World.Get and pool Modify too. A dead handle in World.Get has its own row (stale-entity) as well.
            list.Add(new Claim {
                Id = "debug-table/get-missing", DebugCheck = true, Since = "2.2.2",
                Quote = "`Get` (world or pool) or pool `Modify` of a component the entity does not have | `IndexOutOfRangeException` on Mono and .NET; on IL2CPP, which skips bounds checks there, a read outside the pool's arrays, and a write through the `ref` corrupts memory | throws",
                Phrases = new[] { "Get (world or pool) or pool Modify of a component the entity does not have | IndexOutOfRangeException on Mono and .NET" },
                Applies = cc => (cc.TargetType == typeof(ComponentPool<CA>) && (cc.Name == "Get" || cc.Name == "Modify")
                        && (cc.Kind == "stale-index" || cc.Kind == "neg" || cc.Kind == "min" || cc.Kind == "huge" || cc.Scenario == Scenarios.WorldDestroyed))
                    || (cc.IsWorld && !cc.Member.IsExtension && cc.Name == "Get" && cc.DeadEntityArg),
                Check = cc => {
                    if (dbg) {
                        return MustThrowInDebug(cc, Verdict.Ok, null);
                    }
                    if (cc.Threw && cc.Outcome.Class == "IndexOOR") {
                        return ClaimCheck.Ok(Verdict.Doc, "release: IndexOutOfRangeException, as documented for Mono and .NET");
                    }
                    // The row describes IL2CPP as a read outside the arrays: whatever else happens is not promised either way.
                    return ClaimCheck.Ok(Verdict.Undefined, "release: " + (cc.Threw ? cc.Outcome.ExShort : "returned " + cc.Outcome.Value)
                        + " where the docs describe IndexOutOfRangeException on .NET and an out-of-bounds read on IL2CPP");
                }
            });
            list.Add(new Claim {
                Id = "debug-table/remove-missing",
                Quote = "`Remove` of a missing component | no-op | no-op",
                Phrases = new[] { "Remove of a missing component | no-op | no-op" },
                Applies = cc => (cc.IsWorld && cc.Curated != null && cc.Curated.StartsWith("remove-missing", StringComparison.Ordinal))
                    || (cc.TargetType == typeof(ComponentPool<CA>) && cc.Name == "Remove" && cc.Kind == "stale-index" && cc.LiveWorld),
                Check = cc => NoThrowNoChange(cc, "Remove of a missing component")
            });
            list.Add(new Claim {
                Id = "debug-table/destroy-dead",
                Quote = "`DestroyEntity` of a dead entity | no-op | no-op",
                Phrases = new[] { "DestroyEntity of a dead entity | no-op | no-op" },
                Applies = cc => cc.IsWorld && cc.Name == "DestroyEntity" && cc.LiveWorld && cc.DeadEntityArg,
                Check = cc => NoThrowNoChange(cc, "DestroyEntity of a dead entity")
            });
            list.Add(new Claim {
                Id = "debug-table/copy-dead", DebugCheck = true,
                Quote = "`CopyEntity` of a dead entity | returns `Entity.Null` | throws",
                Phrases = new[] { "CopyEntity of a dead entity | returns Entity.Null | throws" },
                Applies = cc => cc.IsWorld && cc.Name == "CopyEntity" && cc.LiveWorld && cc.DeadEntityArg,
                Check = cc => {
                    if (dbg) {
                        return MustThrowInDebug(cc, Verdict.Ok, null);
                    }
                    if (cc.Threw) {
                        return ClaimCheck.Bad("release: the docs promise Entity.Null, but the call threw " + cc.Outcome.ExShort);
                    }
                    if (!(cc.Result is Entity e) || e != Entity.Null) {
                        return ClaimCheck.Bad("release: the docs promise Entity.Null, but the call returned " + cc.Outcome.Value);
                    }
                    // Entity.Null means no copy was made: a side effect before the check (a copy created
                    // and left behind) contradicts it even though the result is right.
                    var changed = cc.Diffs?.Where(d => !d.Extended).ToList();
                    if (changed != null && changed.Count > 0) {
                        return ClaimCheck.Bad("release: the call returned Entity.Null as documented, but the state changed (no copy may be made): " + DiffText(changed), "high");
                    }
                    return ClaimCheck.Ok();
                }
            });
            list.Add(new Claim {
                Id = "debug-table/getentity-dead-slot",
                Quote = "`GetEntity` on a dead slot | dead handle | dead handle",
                Phrases = new[] { "GetEntity on a dead slot | dead handle | dead handle" },
                Applies = cc => cc.IsWorld && cc.Name == "GetEntity" && cc.Kind == "stale-index" && cc.Scenario == Scenarios.StaleDead,
                Check = cc => {
                    if (cc.Threw) {
                        return ClaimCheck.Bad("the docs promise a dead handle, but the call threw " + cc.Outcome.ExShort);
                    }
                    var e = (Entity)cc.Result;
                    if (cc.Ctx.World.IsAlive(e) || e != cc.Ctx.Stale) {
                        return ClaimCheck.Bad("the docs promise the handle of the entity that lived in the slot, but the call returned " + e + " (stale " + cc.Ctx.Stale + ")");
                    }
                    return ClaimCheck.Ok();
                }
            });
            list.Add(new Claim {
                Id = "debug-table/runner-run-before-init", DebugCheck = true,
                Quote = "Runner: `Run` before `Init`, `Add` after `Init`, unknown name | silent | throws (2.2.2: Runner: `Run` before `Init` | not checked: the systems run without their `Init` | throws)",
                Phrases = new[] { "Runner: Run before Init, Add after Init, unknown name | silent | throws",
                                  "Runner: Run before Init | not checked: the systems run without their Init | throws" },
                Applies = cc => cc.TargetType == typeof(SystemsRunner) && cc.Name == "Run" && cc.Scenario == Scenarios.RunnerDestroyed && cc.Variant == Variant.None,
                Check = cc => dbg ? MustThrowInDebug(cc, Verdict.Ok, null)
                    : cc.Threw ? ClaimCheck.Bad("release: the docs promise silence, but Run threw " + cc.Outcome.ExShort) : ClaimCheck.Ok()
            });
            list.Add(new Claim {
                Id = "debug-table/runner-add-after-init", DebugCheck = true,
                Quote = "Runner: `Run` before `Init`, `Add` after `Init`, unknown name | silent | throws (2.2.2: Runner: `Add` after `Init` | not checked: `Init` is not called for the system | throws)",
                Phrases = new[] { "Runner: Run before Init, Add after Init, unknown name | silent | throws",
                                  "Runner: Add after Init | not checked: Init is not called for the system | throws" },
                Applies = cc => cc.TargetType == typeof(SystemsRunner) && (cc.Name == "Add" || cc.Name == "AddGroup" || cc.Name == "AddPhase")
                    && (cc.Scenario == Scenarios.Fresh || cc.Scenario == Scenarios.RunnerReinit) && cc.Variant == Variant.None && cc.Curated == null,
                Check = cc => dbg ? MustThrowInDebug(cc, Verdict.Ok, null)
                    : cc.Threw ? ClaimCheck.Bad("release: the docs promise silence, but " + cc.Name + " after Init threw " + cc.Outcome.ExShort) : ClaimCheck.Ok()
            });
            list.Add(new Claim {
                Id = "debug-table/runner-unknown-name", DebugCheck = true,
                Quote = "Runner: `Run` before `Init`, `Add` after `Init`, unknown name | silent | throws (2.2.2: Runner: unknown name in `SetActive` / `IsActive` / `GetRunner` | ignored: does nothing, returns `false`, returns `null` | throws)",
                Phrases = new[] { "Runner: Run before Init, Add after Init, unknown name | silent | throws",
                                  "Runner: unknown name in SetActive / IsActive / GetRunner | ignored: does nothing, returns false, returns null | throws" },
                Applies = cc => cc.Curated == "unknown-name",
                Check = cc => {
                    if (dbg) {
                        return MustThrowInDebug(cc, Verdict.Ok, null);
                    }
                    if (cc.Threw) {
                        return ClaimCheck.Bad("release: an unknown name must be ignored silently, but " + cc.Name + " threw " + cc.Outcome.ExShort);
                    }
                    if (cc.Name == "IsActive" && !(cc.Result is bool b && !b)) {
                        return ClaimCheck.Bad("release: IsActive of an unknown name must return false, it returned " + cc.Outcome.Value);
                    }
                    if (cc.Name == "GetRunner" && cc.Result != null) {
                        return ClaimCheck.Bad("release: GetRunner of an unknown name must return null, it returned " + cc.Outcome.Value);
                    }
                    return NoThrowNoChange(cc, cc.Name + " with an unknown name");
                }
            });
            list.Add(new Claim {
                Id = "debug-table/runner-setactive-no-run", DebugCheck = true, Since = "2.2.0",
                Quote = "Runner: `SetActive` on a system without `Run` (Init/Destroy only) | no-op | throws (2.2.2: Runner: `SetActive` / `IsActive` on a system without `Run` (Init/Destroy only) | does nothing / returns `false` | throws)",
                Phrases = new[] { "Runner: SetActive on a system without Run (Init/Destroy only) | no-op | throws",
                                  "Runner: SetActive / IsActive on a system without Run (Init/Destroy only) | does nothing / returns false | throws" },
                Applies = cc => cc.Curated == "setactive-no-run",
                Check = cc => dbg ? MustThrowInDebug(cc, Verdict.Ok, null) : NoThrowNoChange(cc, "SetActive of a system without Run")
            });
            list.Add(new Claim {
                Id = "debug-table/runner-nested-other-world", DebugCheck = true,
                Quote = "Nested runner with a different World / SharedData | silently ignored | throws",
                Phrases = new[] { "Nested runner with a different World / SharedData | silently ignored | throws" },
                Applies = cc => cc.Curated == "nested-other-world",
                Check = cc => dbg ? MustThrowInDebug(cc, Verdict.Ok, null)
                    : cc.Threw ? ClaimCheck.Bad("release: the docs promise to ignore the other World silently, but the call threw " + cc.Outcome.ExShort) : ClaimCheck.Ok()
            });
            list.Add(new Claim {
                Id = "lifecycle/destroyed-create-pool", DebugCheck = true, Since = "2.2.0",
                Quote = "`CreateEntity` / `Pool<T>` after `World.Destroy` | `NullReferenceException` | `ObjectDisposedException`",
                Phrases = new[] { "CreateEntity / Pool<T> after World.Destroy | NullReferenceException | ObjectDisposedException", "in release they fail with NullReferenceException" },
                Applies = cc => cc.Scenario == Scenarios.WorldDestroyed && cc.Threw
                    && ((cc.IsWorld && (cc.Name == "CreateEntity" || cc.Name == "Pool") && !cc.Member.IsExtension)
                        || cc.PassesThrough("KenseiECS.World.Pool", "KenseiECS.World.CreateEntity", "KenseiECS.World.CreateEntityInternal")),
                Check = cc => {
                    var cls = cc.Outcome.Class;
                    bool direct = cc.IsWorld && (cc.Name == "CreateEntity" || cc.Name == "Pool");
                    string how = direct ? "" : " (through " + (cc.PassesThrough("KenseiECS.World.Pool") ? "World.Pool" : "World.CreateEntity") + ")";
                    if (cls == "ODE") {
                        return ClaimCheck.Ok(Verdict.Ok, "ObjectDisposedException" + how);
                    }
                    if (dbg) {
                        return ClaimCheck.Bad("under KENSEI_DEBUG the docs promise ObjectDisposedException" + how + ", but the call threw " + cc.Outcome.ExShort);
                    }
                    if (cls == "NRE") {
                        return ClaimCheck.Ok(Verdict.Doc, "release: the NullReferenceException is documented" + how);
                    }
                    return ClaimCheck.Bad("release: the docs promise NullReferenceException" + how + ", but the call threw " + cc.Outcome.ExShort);
                }
            });
            list.Add(new Claim {
                Id = "lifecycle/destroyed-reserve-filter", Since = "2.2.0",
                Quote = "`World.Reserve` / `Filter` after `World.Destroy` | `ObjectDisposedException` | `ObjectDisposedException`",
                Phrases = new[] { "World.Reserve / Filter after World.Destroy | ObjectDisposedException | ObjectDisposedException", "Reserve and filter creation throw ObjectDisposedException" },
                Applies = cc => cc.Scenario == Scenarios.WorldDestroyed && cc.Variant == Variant.None
                    && ((cc.IsWorld && (cc.Name == "Reserve" || (cc.Name == "Filter" && cc.Member.Method is System.Reflection.MethodInfo mi && mi.ReturnType == typeof(Filter))))
                        || (cc.TargetType == typeof(FilterBuilder) && cc.Name == "End")),
                Check = cc => cc.Outcome.Class == "ODE" ? ClaimCheck.Ok()
                    : ClaimCheck.Bad("the docs promise ObjectDisposedException, but the call " + (cc.Threw ? "threw " + cc.Outcome.ExShort : "returned " + cc.Outcome.Value))
            });
            list.Add(new Claim {
                Id = "lifecycle/destroyed-clear", Since = "2.2.1",
                Quote = "`Clear`, `Reserve` and creating a filter throw `ObjectDisposedException` (after `World.Destroy`)",
                Phrases = new[] { "Clear, Reserve and creating a filter throw ObjectDisposedException", "Clear, Reserve and filter creation throw ObjectDisposedException" },
                Applies = cc => cc.Scenario == Scenarios.WorldDestroyed && cc.Variant == Variant.None && cc.IsWorld && cc.Name == "Clear",
                Check = cc => cc.Outcome.Class == "ODE" ? ClaimCheck.Ok()
                    : ClaimCheck.Bad("Clear after Destroy: the docs promise ObjectDisposedException, but the call " + (cc.Threw ? "threw " + cc.Outcome.ExShort : "returned " + cc.Outcome.Value))
            });
            list.Add(new Claim {
                Id = "lifecycle/destroyed-isalive-destroyentity", Since = "2.2.1",
                Quote = "Even after such a failed `CreateEntity`, `IsAlive` returns `false` and `DestroyEntity` does nothing for any handle, as on any destroyed world.",
                Phrases = new[] { "IsAlive returns false and DestroyEntity does nothing for any handle" },
                Applies = cc => cc.Scenario == Scenarios.WorldDestroyed && cc.IsWorld && (cc.Name == "IsAlive" || cc.Name == "DestroyEntity"),
                Check = cc => {
                    if (cc.Threw) {
                        return ClaimCheck.Bad(cc.Name + " on a destroyed world threw " + cc.Outcome.ExShort + ", but the docs promise false / nothing");
                    }
                    if (cc.Name == "IsAlive" && !(cc.Result is bool b && !b)) {
                        return ClaimCheck.Bad("IsAlive on a destroyed world returned " + cc.Outcome.Value);
                    }
                    return ClaimCheck.Ok();
                }
            });
            list.Add(new Claim {
                Id = "xml/destroy-twice",
                Quote = "Calling it again is a no-op",
                Phrases = new[] { "Calling it again is a no-op" },
                Applies = cc => cc.IsWorld && cc.Name == "Destroy" && cc.Scenario == Scenarios.WorldDestroyed,
                Check = cc => cc.Threw ? ClaimCheck.Bad("a second Destroy threw " + cc.Outcome.ExShort) : ClaimCheck.Ok()
            });
            list.Add(new Claim {
                Id = "xml/is-destroyed",
                Quote = "IsDestroyed: True after Destroy has been called.",
                Phrases = new[] { "True after Destroy has been called" },
                Applies = cc => cc.IsWorld && cc.Name == "IsDestroyed",
                Check = cc => {
                    bool expected = cc.Scenario == Scenarios.WorldDestroyed;
                    return !cc.Threw && cc.Result is bool b && b == expected ? ClaimCheck.Ok()
                        : ClaimCheck.Bad("IsDestroyed returned " + cc.Outcome.Signature + ", expected " + expected);
                }
            });
            list.Add(new Claim {
                Id = "xml/tryget-safe",
                Quote = "TryGetEntity: Handle for a slot index if that slot currently holds an alive entity. Safe for any index.",
                Phrases = new[] { "Safe for any index" },
                Applies = cc => cc.IsWorld && cc.Name == "TryGetEntity",
                Check = cc => cc.Threw ? ClaimCheck.Bad("TryGetEntity threw " + cc.Outcome.ExShort + ", but the docs promise \"Safe for any index\"", "medium") : ClaimCheck.Ok()
            });
            list.Add(new Claim {
                Id = "xml/does-not-create-pool",
                Quote = "Has / Remove: Does not create the pool.",
                Phrases = new[] { "Does not create the pool" },
                Applies = cc => cc.Curated == "no-pool",
                Check = cc => {
                    if (cc.Threw) {
                        return ClaimCheck.Bad(cc.Name + " threw " + cc.Outcome.ExShort);
                    }
                    var pool = cc.Ctx.World.GetPool(ComponentType<CZ>.Index);
                    return pool == null ? ClaimCheck.Ok() : ClaimCheck.Bad(cc.Name + "<CZ> created the pool, but the docs say \"Does not create the pool\"");
                }
            });
            list.Add(new Claim {
                Id = "xml/add-already-has",
                Quote = "Add: Throws if the entity already has it.",
                Phrases = new[] { "Throws if the entity already has it" },
                Applies = cc => cc.Curated == "already-has",
                Check = cc => cc.Threw ? ClaimCheck.Ok() : ClaimCheck.Bad("a second Add did not throw")
            });
            list.Add(new Claim {
                Id = "xml/singleton",
                Quote = "GetSingleton: Throws unless exactly one entity has it.",
                Phrases = new[] { "Throws unless exactly one entity has it" },
                Applies = cc => cc.IsWorld && (cc.Name == "GetSingleton" || cc.Name == "GetSingletonEntity") && cc.Scenario == Scenarios.Fresh,
                Check = cc => cc.Threw ? ClaimCheck.Ok() : ClaimCheck.Bad(cc.Name + " did not throw with two entities that have CA")
            });
            list.Add(new Claim {
                Id = "xml/filter-first-empty",
                Quote = "First: Throws when the filter is empty.",
                Phrases = new[] { "Throws when the filter is empty" },
                Applies = cc => cc.Curated == "empty-filter" && cc.Name == "First",
                Check = cc => cc.Threw ? ClaimCheck.Ok() : ClaimCheck.Bad("First() on an empty filter returned " + cc.Outcome.Value)
            });
            list.Add(new Claim {
                Id = "xml/filter-single",
                Quote = "Single: Throws unless exactly one entity matches.",
                Phrases = new[] { "Throws unless exactly one entity matches" },
                Applies = cc => cc.TargetType == typeof(Filter) && cc.Name == "Single" && cc.Scenario == Scenarios.Fresh,
                Check = cc => cc.Threw ? ClaimCheck.Ok() : ClaimCheck.Bad("Single() with two matching entities returned " + cc.Outcome.Value)
            });
            list.Add(new Claim {
                Id = "xml/shared-get-missing",
                Quote = "SharedData.Get: Throws if not found. TryGet: Returns false if not registered.",
                Phrases = new[] { "Throws if not found" },
                Applies = cc => cc.Curated == "missing-key",
                Check = cc => {
                    if (cc.Name == "Get") {
                        return cc.Threw ? ClaimCheck.Ok() : ClaimCheck.Bad("Get of an unregistered key did not throw");
                    }
                    return !cc.Threw && cc.Result is bool b && !b ? ClaimCheck.Ok() : ClaimCheck.Bad("TryGet of an unregistered key: " + cc.Outcome.Signature);
                }
            });
            list.Add(new Claim {
                Id = "xml/load-requires-empty",
                Quote = "Load: Restore a snapshot into an empty world. Throws if the world has entities or the stream is not a snapshot.",
                Phrases = new[] { "Throws if the world has entities or the stream is not a snapshot" },
                Applies = cc => cc.TargetType == typeof(WorldSerializer) && cc.Name == "Load"
                    && ((cc.Scenario == Scenarios.Fresh && cc.Curated == null) || cc.Curated == "load-garbage" || cc.Curated == "load-empty-stream" || cc.Curated == "load-truncated"),
                Check = cc => cc.Threw ? ClaimCheck.Ok() : ClaimCheck.Bad("Load did not throw: " + cc.Outcome.Signature)
            });
            list.Add(new Claim {
                Id = "docs/load-unknown-type",
                Quote = "Load ... also throws when the stream is not a snapshot, has an unsupported version, or names a component type that is not loaded in the process.",
                Phrases = new[] { "names a component type that is not loaded in the process" },
                Applies = cc => cc.Curated == "load-unknown-type",
                Check = cc => cc.Threw ? ClaimCheck.Ok() : ClaimCheck.Bad("Load of a snapshot with an unknown component type did not throw: " + cc.Outcome.Signature)
            });
            list.Add(new Claim {
                Id = "xml/commandbuffer-dead-skipped",
                Quote = "Commands targeting an Entity that is dead at Playback time are skipped.",
                Phrases = new[] { "Commands targeting an Entity that is dead at Playback time are skipped" },
                Applies = cc => cc.TargetType == typeof(CommandBuffer) && cc.LiveWorld && cc.DeadEntityArg
                    && (cc.Name == "Add" || cc.Name == "Set" || cc.Name == "Remove" || cc.Name == "DestroyEntity"),
                Check = cc => NoThrowNoChange(cc, "a command on a dead entity at Playback")
            });
            list.Add(new Claim {
                Id = "xml/runner-destroy-uninitialized",
                Quote = "Destroy runs IDestroySystem in reverse registration order, is a no-op on an uninitialized runner, and makes the runner re-initializable.",
                Phrases = new[] { "is a no-op on an uninitialized runner", "No-op if Init has not completed" },
                Applies = cc => cc.TargetType == typeof(SystemsRunner) && cc.Name == "Destroy" && cc.Scenario == Scenarios.RunnerDestroyed && cc.Variant == Variant.None,
                Check = cc => NoThrowNoChange(cc, "Destroy of an uninitialized runner")
            });
            list.Add(new Claim {
                Id = "xml/runner-reinitializable",
                Quote = "Destroy ... makes the runner re-initializable.",
                Phrases = new[] { "makes the runner re-initializable", "afterwards the runner can be initialized again" },
                Applies = cc => cc.TargetType == typeof(SystemsRunner) && cc.Name == "Init" && cc.Scenario == Scenarios.RunnerDestroyed && cc.Variant == Variant.None,
                Check = cc => {
                    if (cc.Threw) {
                        return ClaimCheck.Bad("Init after Destroy threw " + cc.Outcome.ExShort);
                    }
                    var r = cc.Ctx.Runner;
                    return r.IsInitialized && cc.Ctx.Sys.Inits == 2 ? ClaimCheck.Ok()
                        : ClaimCheck.Bad("after Destroy and Init: IsInitialized=" + r.IsInitialized + ", the system's Init ran " + cc.Ctx.Sys.Inits + " time(s), expected 2");
                }
            });
            list.Add(new Claim {
                Id = "xml/runner-init-noop",
                Quote = "Init: Invokes Init() on all IInitSystem in registration order; no-op if already initialized.",
                Phrases = new[] { "no-op if already initialized" },
                Applies = cc => cc.TargetType == typeof(SystemsRunner) && cc.Name == "Init" && (cc.Scenario == Scenarios.Fresh || cc.Scenario == Scenarios.RunnerReinit) && cc.Variant == Variant.None,
                Check = cc => NoThrowNoChange(cc, "a second Init")
            });
            list.Add(new Claim {
                Id = "xml/indexof-none", Since = "2.2.0",
                Quote = "IndexOf: ... -1 when there is none / -1 when it is not registered in this runner.",
                Phrases = new[] { "-1 when there is none", "or -1 when it is not registered" },
                Applies = cc => cc.Curated == "indexof-none",
                Check = cc => !cc.Threw && cc.Result is int i && i == -1 ? ClaimCheck.Ok() : ClaimCheck.Bad("IndexOf of something unregistered: " + cc.Outcome.Signature)
            });
            list.Add(new Claim {
                Id = "xml/listeners-null-values", Since = "2.2.0",
                Quote = "Invoke: ... Nothing when Values is null.",
                Phrases = new[] { "Nothing when Values is null" },
                Applies = cc => cc.Curated == "listeners-default",
                Check = cc => cc.Threw ? ClaimCheck.Bad("Invoke on Listeners with Values == null threw " + cc.Outcome.ExShort) : ClaimCheck.Ok()
            });
            list.Add(new Claim {
                Id = "xml/addevent-stale", DebugCheck = true,
                Quote = "AddEvent: Under KENSEI_DEBUG a stale handle throws, whether or not the slot's new occupant has a buffer. (2.2.2: Under KENSEI_DEBUG a handle that is not alive throws (whether or not the slot's new occupant has a buffer), even while another entity is being destroyed.)",
                Phrases = new[] { "Under KENSEI_DEBUG a stale handle throws", "Under KENSEI_DEBUG a handle that is not alive throws" },
                Applies = cc => cc.IsWorld && cc.Name == "AddEvent" && cc.Kind == "stale",
                Check = cc => MustThrowInDebug(cc, Verdict.Undefined, "release: not checked")
            });
            list.Add(new Claim {
                Id = "xml/world-config-fallback",
                Quote = "World(WorldConfig): Non-positive values fall back to WorldConfig.Default.",
                Phrases = new[] { "Non-positive values fall back to" },
                Applies = cc => cc.IsWorld && cc.Member.IsCtor && (cc.Kind == "fields-neg" || cc.Kind == "default"),
                Check = cc => cc.Threw ? ClaimCheck.Bad("World with non-positive capacities threw " + cc.Outcome.ExShort) : ClaimCheck.Ok()
            });

            // --- owning groups (the curated Group<...>() cases: a type another group owns, a type listed twice) ---
            list.Add(new Claim {
                Id = "docs/group-second-owner-throws",
                Quote = "A pool can be owned by one group; a second group over the same type throws. (guides/groups.md) RegisterGroup throws if any pool already has an owner or if a type is listed twice (architecture.md)",
                Phrases = new[] { "a second group over the same type throws", "RegisterGroup throws if any pool already has an owner or if a type is listed twice" },
                Applies = cc => cc.Curated != null && cc.Curated.StartsWith("group-", StringComparison.Ordinal),
                Check = cc => {
                    if (!cc.Threw) {
                        return ClaimCheck.Bad("the group was registered over a type another group owns (or listed twice): " + cc.Outcome.Value, "high");
                    }
                    return cc.Outcome.Suspicious ? ClaimCheck.Ok(Verdict.Doc, "throws as documented, but " + cc.Outcome.ExShort) : ClaimCheck.Ok();
                }
            });

            // --- the safe queries after a call (State claims) ---
            list.Add(new Claim {
                Id = "docs/group-aligned", Kind = ClaimKind.State,
                Quote = "An owning group keeps the dense arrays of its pools aligned, so every member sits at the same index in each pool, packed at the front. Membership is exact and follows Add/Remove/DestroyEntity/Clear. (guides/groups.md)",
                Phrases = new[] { "keeps the dense arrays of its pools aligned" },
                Applies = cc => cc.Curated != null && cc.Curated.StartsWith("group-", StringComparison.Ordinal),
                Violations = (cc, diffs) => diffs.Where(d => d.Probe == Matrix.GroupAlignedProbe).ToList()
            });
            list.Add(new Claim {
                Id = "docs/group-registration-atomic", Kind = ClaimKind.State,
                Quote = "RegisterGroup throws if any pool already has an owner or if a type is listed twice, sets _ownerGroup on every pool (which flips _hasHooks), adds the group to _groups and calls Populate() (architecture.md); a pool can be owned by one group (guides/groups.md): a registration that throws owns nothing, so a type no registered group owns can get a group",
                Phrases = new[] { "RegisterGroup throws if any pool already has an owner or if a type is listed twice, sets _ownerGroup on every pool" },
                Applies = cc => cc.Curated != null && cc.Curated.StartsWith("group-", StringComparison.Ordinal),
                Violations = (cc, diffs) => diffs.Where(d => d.Probe == Matrix.GroupFreeProbe).ToList()
            });
            list.Add(new Claim {
                Id = "state/destroyed-isalive-destroyentity", Kind = ClaimKind.State, Since = "2.2.1",
                Quote = "Even after such a failed `CreateEntity`, `IsAlive` returns `false` and `DestroyEntity` does nothing for any handle, as on any destroyed world.",
                Phrases = new[] { "IsAlive returns false and DestroyEntity does nothing for any handle" },
                Applies = cc => cc.Scenario == Scenarios.WorldDestroyed,
                Violations = (cc, diffs) => diffs.Where(d => IsProbe(d, "IsAlive(", "DestroyEntity(")).ToList()
            });
            list.Add(new Claim {
                Id = "state/tryget-safe", Kind = ClaimKind.State,
                Quote = "TryGetEntity: ... Safe for any index.",
                Phrases = new[] { "Safe for any index" },
                Applies = cc => cc.WorldStillLive,
                Violations = (cc, diffs) => diffs.Where(d => IsProbe(d, "TryGetEntity(") && d.Expected.StartsWith("ok:", StringComparison.Ordinal) && d.Actual.StartsWith("ex:", StringComparison.Ordinal)).ToList()
            });
            list.Add(new Claim {
                Id = "state/destroy-dead-noop", Kind = ClaimKind.State,
                Quote = "`DestroyEntity` of a dead entity | no-op | no-op",
                Phrases = new[] { "DestroyEntity of a dead entity | no-op | no-op" },
                Applies = cc => cc.WorldStillLive,
                Violations = (cc, diffs) => diffs.Where(d => IsProbe(d, "DestroyEntity(") && d.Expected.StartsWith("ok:", StringComparison.Ordinal) && d.Actual.StartsWith("ex:", StringComparison.Ordinal)).ToList()
            });
            list.Add(new Claim {
                Id = "docs/alive-iff-component", Kind = ClaimKind.State,
                Quote = "An entity is alive if and only if it has at least one component (the last Remove destroys it).",
                Phrases = new[] { "alive if and only if it has at least one" },
                Applies = cc => cc.LiveWorld,
                Violations = (cc, diffs) => diffs.Where(d => d.Probe.EndsWith(Probes.AliveWithoutComponents, StringComparison.Ordinal)
                    && d.Actual.StartsWith("ok:", StringComparison.Ordinal) && d.Actual != "ok:0").ToList()
            });

            // --- documented partial effects (Partial claims) ---
            list.Add(new Claim {
                Id = "partial/playback", Kind = ClaimKind.Partial,
                Quote = "CommandBuffer.Playback clears the buffer in a finally; a throwing command discards the rest.",
                Phrases = new[] { "a throwing command discards the rest", "If a command throws, the remaining commands are discarded" },
                Applies = cc => cc.TargetType == typeof(CommandBuffer) && cc.Name == "Playback"
            });
            list.Add(new Claim {
                Id = "partial/runner", Kind = ClaimKind.Partial,
                Quote = "SystemsRunner.Init records progress...; Run cleans OneFrame components in a finally.",
                Phrases = new[] { "Run cleans OneFrame components in a finally", "records progress in _initProgress" },
                Applies = cc => cc.TargetType == typeof(SystemsRunner) && (cc.Name == "Run" || cc.Name == "Init")
            });
            list.Add(new Claim {
                Id = "partial/destroy-entity", Kind = ClaimKind.Partial, CoversInvariant = true,
                Quote = "If a listener or AutoReset throws, the slot is still released, but components not yet removed stay orphaned in their pools and filters.",
                Phrases = new[] { "If a listener or AutoReset throws, the slot is still released" },
                Applies = cc => cc.IsWorld && cc.Name == "DestroyEntity"
            });
            list.Add(new Claim {
                Id = "partial/load", Kind = ClaimKind.Partial, CoversInvariant = true,
                Quote = "WorldSerializer.Load restores SuppressEvents(false) in a finally; a malformed stream leaves a partially restored world, which the caller should Clear().",
                Phrases = new[] { "a malformed stream leaves a partially restored world" },
                Applies = cc => cc.TargetType == typeof(WorldSerializer) && cc.Name == "Load"
            });
            return list;
        }
    }
}
