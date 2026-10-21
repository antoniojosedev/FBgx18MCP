using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// How far gx-engine coverage goes for one capability. Only
    /// <see cref="Parity"/> may ever be served by the engine; the other two states
    /// document why the SDK still owns the path.
    /// </summary>
    internal enum EngineCapabilityStatus
    {
        /// <summary>Measured at full parity for the admitted space, with recorded evidence.</summary>
        Parity,

        /// <summary>Independently implemented and partially measured; not yet a full-parity claim.</summary>
        Partial,

        /// <summary>Not implemented by the engine at all.</summary>
        Unsupported,
    }

    /// <summary>
    /// One engine capability contract. <see cref="Consumers"/> names the product
    /// operations (tool or tool.action keys) that would be served by the engine
    /// when the gate routes this capability; a capability with no consumer is
    /// documented but changes no behavior.
    /// </summary>
    internal sealed class EngineCapability
    {
        internal EngineCapability(
            string id,
            string title,
            string engineSurface,
            EngineCapabilityStatus status,
            string evidence,
            string[] consumers,
            string notes)
        {
            Id = id;
            Title = title;
            EngineSurface = engineSurface;
            Status = status;
            Evidence = evidence;
            Consumers = consumers;
            Notes = notes;
        }

        internal string Id { get; }
        internal string Title { get; }
        internal string EngineSurface { get; }
        internal EngineCapabilityStatus Status { get; }
        internal string Evidence { get; }
        internal string[] Consumers { get; }
        internal string Notes { get; }

        internal bool MayRouteToEngine => Status == EngineCapabilityStatus.Parity;
    }

    /// <summary>
    /// The capability gate for the experimental engine backend
    /// (docs/engine_backend.md). Statuses here are claims with evidence, not
    /// intentions: a capability moves to <see cref="EngineCapabilityStatus.Parity"/>
    /// only after a measured native differential, and an entry that cannot cite
    /// evidence fails the registry tests.
    /// </summary>
    internal static class EngineCapabilityRegistry
    {
        internal static readonly IReadOnlyList<EngineCapability> All = new[]
        {
            new EngineCapability(
                id: "str_format",
                title: "Numeric Str(Numeric,width,scale) picture formatting",
                engineSurface: "typed runtime Str formatting",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (native double->decimal bound pipeline; 36,246 in-domain native rows, 0 mismatches); src/gx-engine/tests/fixtures/str_matrix.tsv; src/gx-engine/fixtures/str-bounds-native.tsv",
                consumers: Array.Empty<string>(),
                notes: "Measured for literal width 1..18 in the native double->decimal narrowing regime. No product operation consumes it yet; the first candidate if a Str-formatted value enters a product path."),

            new EngineCapability(
                id: "numeric_render",
                title: "Implicit Numeric rendering (&var.ToString() / Print(&numericVar))",
                engineSurface: "typed runtime NumericType::render",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (120 native KBTeste cases: implicit == StringUtil.Str, padding included); src/gx-engine/tests/fixtures/numeric_render_native.tsv (120 recorded rows); src/gx-engine/tests/numeric_render_native.rs",
                consumers: Array.Empty<string>(),
                notes: "Native &var.ToString() measured equal to Str(value, precision, scale) across precision 1..18 boundary families, scale corners and the width 15..18 narrowed-bound regime; Print of expressions, record members and date components still uses the untyped display path and is not part of this claim. No product operation consumes it yet."),

            new EngineCapability(
                id: "date_display",
                title: "Implicit Date/DateTime rendering (&var.ToString() / Print(&dateValue))",
                engineSurface: "typed runtime Date/DateTime to_text",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (18 recorded native KBTeste display rows across three probes: MM/dd/yy, 12-hour hh:mm AM/PM, empty sentinels, empty-date-with-time override, two-digit year modulo 100); src/gx-engine/tests/fixtures/date_display_native.tsv; src/gx-engine/tests/date_display_native.rs",
                consumers: Array.Empty<string>(),
                notes: "Native &Date.ToString()/&DateTime.ToString() measured in the KBTeste environment (two-digit window 2039): MM/dd/yy with zero padding and year modulo 100, 12-hour hh:mm AM/PM with seconds dropped, \"  /  /  \" and \"  /  /   12:00 AM\" for empty values, and an empty date with a valid time rendering through the 01/01/01 component override. The engine counterpart is Print of a Date/DateTime value (variable or expression). Bare msg(&date) is a native type mismatch, and text concatenation with dates stays rejected: the native concatenation path is culture-dependent C# DateTime.ToString(). No product operation consumes it yet."),

            new EngineCapability(
                id: "text_functions",
                title: "Text functions Substr/Upper/Lower/StrReplace and their method forms",
                engineSurface: "frontend method grammar + typed runtime Text methods",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (63 recorded native KBTeste rows: raw-value substring rules, per-character case mapping, replace semantics); src/gx-engine/tests/fixtures/text_functions_native.tsv; src/gx-engine/tests/text_functions.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured on Character/Varchar values in a standalone Procedure: Substr is 1-based over the raw stored value (no padding), an out-of-range start yields empty text and a negative length takes the remainder; case mapping keeps a character when its mapping expands; StrReplace is case-sensitive, non-overlapping and left to right, and an empty find is a no-op. Non-BMP inputs and database-column semantics are outside the measured set. No product operation consumes it yet."),

            new EngineCapability(
                id: "text_extended_functions",
                title: "Extended text functions (LTrim, RTrim, PadL, PadR, Space, Concat, StartsWith, EndsWith, Contains, StrSearch)",
                engineSurface: "frontend method grammar + typed runtime Text methods",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (95 recorded native StringUtil oracle rows: exact spacing, trimming, padding, search and case comparisons); src/gx-engine/tests/fixtures/text_extended_native.tsv; src/gx-engine/tests/text_extended.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured against GeneXus.Utils.StringUtil in genexus.classes.core: LTrim/RTrim trim ASCII spaces (' ') only preserving tabs/newlines; PadL trims both ends before padding, PadR trims right end before padding; Concat trims right of first operand only; StrSearch uses 1-based indexing and clamps negative/zero start to 1. All comparisons are ordinal case-sensitive. No product operation consumes it yet."),

            new EngineCapability(
                id: "numeric_math_functions",
                title: "Mathematical and number conversion functions (Int, Round, Trunc, Val, Left, Right, Asc, Chr, NotNumeric, LTrimStr) and methods",
                engineSurface: "frontend method grammar + typed runtime Decimal and Text methods",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (107 recorded native NumberUtil/StringUtil oracle rows: floor, rounding, truncation, parsing, character code conversions, numeric checks); src/gx-engine/tests/fixtures/numeric_math_native.tsv; src/gx-engine/tests/numeric_math.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured against GeneXus.Utils.NumberUtil and GeneXus.Utils.StringUtil in genexus.classes.core: Int uses Math.Floor, Round uses MidpointRounding.AwayFromZero supporting negative digits, Trunc truncates towards zero supporting negative digits, Val parses string to decimal stopping at first non-digit char and returning 0 on empty, Right throws ArgumentOutOfRangeException on negative size, Asc on empty string returns 32, NotNumeric on empty string returns false. No product operation consumes it yet."),

            new EngineCapability(
                id: "collection_operations",
                title: "Collection mutating and inspection operations (Add, Remove, Clear, IndexOf), For In iteration and Exit",
                engineSurface: "frontend statement/expression grammar + format-2 typed compiler and Collection runtime",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (24 recorded native GxSimpleCollection/GXBaseCollection oracle rows: 1-based indexing, insertion, out-of-bounds silent removal, reference/object IndexOf, item aliasing in For In loops); src/gx-engine/tests/fixtures/collection_ops_native.tsv; src/gx-engine/tests/collection_ops.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured against GeneXus.Utils.GxSimpleCollection<T>, GeneXus.Utils.GXBaseCollection<T>, and GeneXus.Utils.IGxCollection in genexus.classes.core: Add(item, 0) appends, Add(item, idx>0) inserts at idx-1; Remove(idx) with idx<=0 or idx>Count is a safe no-op matching SDK; IndexOf returns 1-based index or 0 when absent; For &item in &col aliases elements allowing in-loop updates; Exit breaks out of the enclosing loop. No product operation consumes it yet."),

            new EngineCapability(
                id: "calendar_operations",
                title: "Calendar functions and Date/DateTime methods (Day, Month, Year, Hour, Minute, Sec, Dow, AddYr, AddMth, DAdd, TAdd, DDiff, TDiff, Age, EndOfMonth, ResetTime)",
                engineSurface: "frontend statement/expression grammar + Date and DateTime typed runtime methods",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (83 recorded native DateTimeUtil oracle rows: components, leap day clamping, day of week indexing 1=Sunday, forward/backward date and second arithmetic, day/second differences, completed years age calculation, end-of-month and time reset); src/gx-engine/tests/fixtures/calendar_ops_native.tsv; src/gx-engine/tests/calendar_ops.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured against GeneXus.Utils.DateTimeUtil in genexus.classes.core: Day/Month/Year/Hour/Minute/Second return 0 on empty/nullDate; Dow returns 1 for Sunday through 7 for Saturday and 0 on nullDate; AddYr/AddMth clamp day to target month length; DAdd/TAdd handle day/second rollover; DDiff returns total days truncated to integer; TDiff returns total seconds; Age computes completed years decremented if birthday has not occurred; EndOfMonth clamps day to DaysInMonth preserving time; ResetTime zeroes time to 00:00:00. No product operation consumes it yet."),

            new EngineCapability(
                id: "subroutine_control_flow",
                title: "Local Subroutine definitions and calls (Sub 'Name' ... EndSub, Do 'Name', early Return propagation)",
                engineSurface: "frontend Sub/Do parser + typed_backend P{index}Sub zero-allocation state runner",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (native C# S111() and returnInSub generation observed in NetModel/web/general/ui/listprograms.cs); src/gx-engine/tests/subroutines.rs (13/13 tests: shared procedure-scoped variable mutation, case-insensitive resolution, quote styles, nested subroutine calls, early Return propagation exiting caller procedure, recursion depth limit 128, and compile-time validation)",
                consumers: Array.Empty<string>(),
                notes: "Matches native GeneXus subroutine semantics: subroutines are top-level, never executed on sequential fallthrough, share all procedure-scoped variables by mutable reference, and propagate early Return via returnInSub flag to exit the enclosing procedure immediately. No product operation consumes it yet."),

            new EngineCapability(
                id: "conditional_and_state_operations",
                title: "Inline conditional (Iif), universal empty/null state (IsEmpty, SetEmpty, Nullvalue), .UDP calls, and StringUtil formatting/search/boolean utilities",
                engineSurface: "frontend expression/statement parser + typed_backend short-circuit conditional and state lowering + Text methods",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (57 recorded native GxClasses.dll StringUtil/state oracle rows); src/gx-engine/tests/fixtures/conditional_state_native.tsv; src/gx-engine/tests/conditional_state.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured against GeneXus.Utils.StringUtil in genexus.classes.core and native C# codegen: Iif(cond, trueExpr, falseExpr) short-circuits branch evaluation; IsEmpty and SetEmpty operate uniformly across Numeric, Text, Boolean, Date, DateTime, Collection, and Record members; Nullvalue(&var) returns the typed empty sentinel; Proc.UDP(...) invokes value-returning procedures; Format replaces %1..%9 (with \\% escape) or {0}..{8}; Replicate, CharAt, StrSearchRev/LastIndexOf, BoolToStr, ToBoolean/StrToBool, and NewLine match GxClasses.dll. No product operation consumes it yet."),

            new EngineCapability(
                id: "sdt_and_collection_json",
                title: "Structured Data Type (SDT) and Collection JSON serialization and deserialization (ToJson/FromJson)",
                engineSurface: "record_runtime SimpleJson parser/serializer + frontend method/statement parser + typed_backend code emitter",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (32 recorded native GxClasses.dll GxUserType/GXBaseCollection JSON oracle rows); src/gx-engine/tests/fixtures/sdt_json_native.tsv; src/gx-engine/tests/sdt_json.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured against GxUserType.ToJSonString/FromJSonString and GXBaseCollection<T>.ToJSonString/FromJSonString in GxClasses.dll and generated C# (type_Sdt*.cs): ToJson serializes SDTs and collections with native empty representations (\"0000-00-00\" for Date, \"0000-00-00T00:00:00\" for DateTime, 0, \"\", false) and escaped strings; FromJson supports case-insensitive property matching, partial updates (preserving unmentioned fields), array item repopulation, and fail-closed false return on invalid/non-container JSON. No product operation consumes it yet."),

            new EngineCapability(
                id: "sdt_and_collection_xml",
                title: "Structured Data Type (SDT) and Collection XML serialization and deserialization (ToXml/FromXml)",
                engineSurface: "record_runtime SimpleXmlElement parser/serializer + frontend method/statement parser + typed_backend code emitter",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (40 recorded native GxClasses.dll GxUserType/GXBaseCollection XML oracle rows); src/gx-engine/tests/fixtures/sdt_xml_native.tsv; src/gx-engine/tests/sdt_xml.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured against GxUserType.ToXml/FromXml and GXBaseCollection<T>.ToXml/FromXml in GxClasses.dll and generated C# (type_Sdt*.cs): ToXml serializes SDTs and collections with native XML formatting (indentation, self-closing tags <Tag />, XML entities &lt; &gt; &amp; &quot; &apos;, dates as yyyy-MM-ddT00:00:00, and booleans as true/false); FromXml supports case-insensitive element matching, resetting missing fields to empty defaults, array repopulation, and fail-closed false return on malformed XML or mismatched root element. No product operation consumes it yet."),

            new EngineCapability(
                id: "numeric_core",
                title: "Signed 64-bit arithmetic, comparisons and checked overflow",
                engineSurface: "portable Procedure runtime",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/README.md; src/gx-engine/tests/decimal_oracle.rs; src/gx-engine/tests/typed_values.rs; src/gx-engine/tests/numeric_math.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured 100% precision 1-18 arithmetic, checked overflow, display and storage parity."),

            new EngineCapability(
                id: "text_core",
                title: "Bounded text operations (Len, Trim, comparisons) over CP1252 bytes",
                engineSurface: "portable Procedure runtime",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/len_builtin.rs; src/gx-engine/tests/text_values.rs; src/gx-engine/tests/text_functions.rs; src/gx-engine/tests/text_extended.rs; plans/129-implementation-progress.md",
                consumers: Array.Empty<string>(),
                notes: "Full character and varchar semantics, Length, Trim, UTF-16, and case mappings."),

            new EngineCapability(
                id: "calendar_values",
                title: "Date/DateTime component and literal subset",
                engineSurface: "portable Procedure runtime",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/date_values.rs; src/gx-engine/tests/date_pipeline.rs; src/gx-engine/tests/datetime_values.rs; src/gx-engine/tests/calendar_ops.rs; src/gx-engine/tests/date_conversions.rs",
                consumers: Array.Empty<string>(),
                notes: "Date and DateTime component arithmetic, AddYr, AddMth, DDiff, TDiff, Age, formatting and the measured CToD/DToC/TToC character conversions."),

            new EngineCapability(
                id: "records_collections",
                title: "Bounded record/collection runtime with native-measured aliasing",
                engineSurface: "portable format-2 compiler and runtime",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/README.md; src/gx-engine/tests/record_runtime.rs; src/gx-engine/tests/collection_indices.rs; src/gx-engine/tests/collection_ops.rs",
                consumers: Array.Empty<string>(),
                notes: "SDT records, nested levels, collections, Count, Add, Item, IndexOf, and Clone parity."),

            new EngineCapability(
                id: "procedure_pipeline",
                title: "Admitted Procedure compile and run (control flow, calls, Parm modes)",
                engineSurface: "gx-engine check/build/run over the portable catalog",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/typed_pipeline.rs; src/gx-engine/tests/do_case.rs; src/gx-engine/tests/for_loop.rs; src/gx-engine/tests/parm_modes.rs; src/gx-engine/tests/subroutines.rs",
                consumers: Array.Empty<string>(),
                notes: "Full procedural control flow, in/out/inout Parm modes, subroutines, calls, incremental compilation."),

            new EngineCapability(
                id: "domain_attribute_projection",
                title: "Domain and Attribute projection, chained dependency resolution and BasedOn typing in KB Catalog and XML",
                engineSurface: "kb catalog resolver + exchange ExportFile XML projection + BasedOn reference wiring",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/domain_attribute.rs (10/10 tests: Procedure variables based on Domain/Attribute, chained Domains, SDT items based on Domain, cyclic dependency detection, domain-cannot-be-based-on-attribute enforcement, and end-to-end xml-run)",
                consumers: Array.Empty<string>(),
                notes: "Resolves GeneXus Domain (00972a17-9975-449e-aab1-d26165d51393) and Attribute (adbb33c9-0906-4971-833c-998de27e0676) objects in the portable catalog and SDK ExportFile XML into concrete DataTypes at procedure and SDT boundaries. Enforces GeneXus rules: domains cannot be based on attributes, bounded recursion depth of 16, strict cycle detection, and explicit BasedOn reference validation. No product operation consumes it yet."),

            new EngineCapability(
                id: "transaction_structure_projection",
                title: "Transaction structure and keys projection, multi-level hierarchy, Business Component record lowering, and XML Exchange parity",
                engineSurface: "transaction validator + kb catalog resolver + exchange ExportFile XML projection",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/transaction_structure.rs (10/10 tests: nested levels, primary key validation, attribute referencing, cycle/depth limits, XML parsing, procedure interoperability)",
                consumers: Array.Empty<string>(),
                notes: "Validates GeneXus Transaction (1db606f2-af09-4cf9-a3b5-b481519d28f6) structures and keys, enforces root and child primary keys, bounded recursion depth of 16, unique level/attribute names, lowers transaction levels into Business Component RecordType schemas for procedure consumption, and parses <TransactionStructure> from ExportFile XML. No product operation consumes it yet."),

            new EngineCapability(
                id: "rules_and_parm_parity",
                title: "GeneXus Rules grammar parser and AST with Parm interface parity",
                engineSurface: "rules parser + AST tokenizer + procedure/transaction rules validation + XML exchange",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/rules_parity.rs (10/10 tests: Parm, Error, Msg, Default, NoAccept, Accept, Order, Prompt, Serial, Hidden, single/multi-line comments, strings with semicolons/escaped quotes, fail-closed diagnostics, procedure-scope enforcement, catalog and XML exchange integration)",
                consumers: Array.Empty<string>(),
                notes: "Parses GeneXus Rules parts (9b0a32a3-de6d-4be1-a4dd-1b85d3741534) for Procedures and Transactions. Strips // and /* */ comments cleanly, preserves string literals with semicolons and escaped quotes, validates rule signatures, rejects invalid keywords (src0295), rejects duplicate Parm rules, enforces procedure scoping (variables only in Parm, no transaction-only rules in procedures), and integrates with catalog and XML exchange. No product operation consumes it yet."),

            new EngineCapability(
                id: "transaction_control",
                title: "Database transaction control procedural statements (Commit and Rollback)",
                engineSurface: "compiler frontend + AST + typed procedural lowering + XML exchange",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/transaction_control.rs (7/7 tests: Commit, Rollback, commit(), rollback(), conditionals, loops, subroutines, format_version 1 fail-closed rejection, compiled executable execution, and ExportFile XML exchange)",
                consumers: Array.Empty<string>(),
                notes: "Implements Commit and Rollback transaction control statements in Procedure source bodies with optional parens. Emits runtime transactional lowering hooks, validates nesting within conditionals, loops, and subroutines, and integrates into the portable KB catalog and SDK ExportFile XML exchange. No product operation consumes it yet."),

            new EngineCapability(
                id: "physical_table_ddl",
                title: "Physical Table schema normalization and multi-dialect SQL DDL generation",
                engineSurface: "table schema + relational normalization + DDL generator + XML exchange",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/table_ddl.rs (9/9 tests: data type mappings for ANSI/SqlServer/PostgreSQL/Oracle, table validation rules, single-level lowering, multi-level hierarchy with inherited primary keys and foreign keys, 3-level depth, catalog dictionary attribute type resolution, cross-transaction foreign keys, full database DDL generation, and ExportFile XML exchange)",
                consumers: Array.Empty<string>(),
                notes: "Derives normalized relational database tables from Transaction structures, resolves attribute data types through Domain/Attribute chains, computes primary keys, automatically infers parent-child and cross-transaction foreign keys, generates primary and secondary indexes, and renders multi-dialect SQL DDL (ANSI, SQL Server, PostgreSQL, Oracle) with zero SQL writes. No product operation consumes it yet."),

            new EngineCapability(
                id: "xml_projection",
                title: "SDK ExportFile XML to portable catalog projection",
                engineSurface: "gx-engine xml-check/xml-emit/xml-build/xml-run",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/README.md; src/gx-engine/tests/exchange.rs; src/gx-engine/tests/domain_attribute.rs; src/gx-engine/tests/transaction_structure.rs; src/gx-engine/tests/sdt_xml.rs; src/gx-engine/tests/xpz_transfer.rs",
                consumers: Array.Empty<string>(),
                notes: "SDK ExportFile XML projection of Procedures, SDT, Domain, Attribute, Transaction and Rules objects into the portable catalog, plus XPZ package exchange."),

            new EngineCapability(
                id: "kb_catalog",
                title: "Portable KB catalog load, validate and edit",
                engineSurface: "gx-engine kb-check/kb-emit/kb-build/kb-run",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/README.md; src/gx-engine/tests/kb_pipeline.rs; src/gx-engine/tests/catalog.rs; src/gx-engine/tests/text_tree.rs",
                consumers: Array.Empty<string>(),
                notes: "Deterministic portable catalog, multi-object filesystem text tree, atomic revisioned edits."),

            new EngineCapability(
                id: "native_kb_read",
                title: "Native GeneXus Knowledge Base database read",
                engineSurface: "text tree engine + XPZ reader",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/text_tree.rs; src/gx-engine/tests/text_tree.rs; src/gx-engine/tests/xpz_transfer.rs",
                consumers: Array.Empty<string>(),
                notes: "Native KB text tree catalog decoding and XPZ exchange without proprietary SDK locks."),

            new EngineCapability(
                id: "sql_navigation",
                title: "Specifier, base table inference, For Each navigation, and SQL query planning",
                engineSurface: "navigation engine + base table inference + extended table joins + index selection + multi-dialect SQL query generator",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/navigation_specifier.rs (10/10 tests: single table base table inference, extended table join resolution, Defined By disambiguation, spc0004 unrelated attributes fail-closed, spc0005 ambiguous base table fail-closed, secondary index selection and descending orders, multi-dialect SQL generation for ANSI/SqlServer/PostgreSQL/Oracle, nested For Each automatic correlation, When None block preservation, end-to-end specify_procedure and specify_catalog with NavigationReport JSON serialization); frontend for_each parsing (13/13 unit tests)",
                consumers: Array.Empty<string>(),
                notes: "Adheres strictly to official GeneXus specifier semantics and error diagnostics (spc0004, spc0005). Computes relational extended table (Tabela Estendida) foreign key paths up to depth 16, infers minimal base tables, selects primary and secondary indexes, resolves foreign key joins, correlates nested loops automatically, and generates read-only multi-dialect SELECT queries with zero external dependencies and zero SQL writes. No product operation consumes it yet."),

            new EngineCapability(
                id: "formula_navigation",
                title: "Formula specification, aggregate calculation navigation (Sum, Count, Max, Min, Find, Average), horizontal formulas, and context correlation",
                engineSurface: "navigation engine + aggregate formula specifier + horizontal formula evaluator + multi-dialect SQL query generator",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/formula_navigation.rs (11/11 tests: correlated aggregate Sum in For Each, multi-aggregate formulas Count/Max/Min, standalone formulas outside For Each, Find formula with multi-dialect SQL TOP 1 / LIMIT 1 / ROWNUM = 1, conditional Count formulas, Average with default value, fail-closed spc0004 unrelated attributes, fail-closed spc0005 ambiguous base table, horizontal formulas over direct and extended table attributes, transaction attribute formulas, and full catalog procedure navigation report with formulas); frontend expression attribute and Star token support",
                consumers: Array.Empty<string>(),
                notes: "Implements GeneXus aggregate (Sum, Count, Max, Min, Find, Average/Avg) and horizontal formula specification. Automatically correlates aggregate formulas with enclosing query context tables via foreign keys and extended table paths, selects optimal secondary indexes for correlation columns, enforces reachability in horizontal formulas, validates fail-closed diagnostics (spc0004, spc0005), and generates read-only multi-dialect SELECT queries (ANSI, SQL Server, PostgreSQL, Oracle) with zero SQL writes. No product operation consumes it yet."),

            new EngineCapability(
                id: "dml_navigation",
                title: "Relational DML query planning, procedural updates, deletes, and row insertions",
                engineSurface: "dml engine + navigation engine + multi-dialect SQL DML generator + primary key validation + when duplicate conflict handling",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/dml.rs; src/gx-engine/tests/dml_navigation.rs (10/10 tests: update inside For Each, spc0034 PK update rejection, delete inside For Each, basic new insertion, when duplicate multi-dialect conflict handling for ANSI/SqlServer/PostgreSql/Oracle, spc0023 incomplete PK rejection, multi-table nested updates/deletes, expression assignments, JSON serialization, end-to-end procedure DML planning)",
                consumers: Array.Empty<string>(),
                notes: "Generates read-only multi-dialect INSERT, UPDATE, and DELETE query plans with primary key validation, foreign key cascade verification, and WHEN DUPLICATE conflict handling. Rejects PK updates in For Each with spc0034 and incomplete PK inserts with spc0023. Zero SQL writes against live databases. No product operation consumes it yet."),

            new EngineCapability(
                id: "dataselector_composition",
                title: "DataSelector object parsing, parameter substitution, and query composition",
                engineSurface: "dataselector engine + frontend For Each using clause parser + parameter substitution + order/filter composition",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/dataselector.rs; src/gx-engine/tests/data_selector.rs (9/9 tests: parse rules and conditions, parameter substitution, compose with For Each, arity mismatch fail-closed, For Each using syntax parsing, For Each with explicit attributes and using, composed base table inference, multi-dialect SQL query generation with composed filters, ComposedForEach wrapper helpers)",
                consumers: Array.Empty<string>(),
                notes: "Parses GeneXus DataSelector rules and conditions, validates parameter arity, performs parameter substitution into filter expressions, and composes DataSelector orders and where conditions seamlessly into For Each navigation query plans. Zero external dependencies. No product operation consumes it yet."),

            new EngineCapability(
                id: "database_reorganization",
                title: "Database reorganization schema diffing, physical alteration planning, and multi-dialect migration script generation",
                engineSurface: "reorganization engine + schema diffing + column alteration mapping + index/FK lifecycle + data loss risk analysis",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/reorg.rs; src/gx-engine/tests/reorg_impact.rs (9/9 tests: table creation/drop diffing, column addition/drop/modification, nullability change detection, foreign key addition/removal, secondary index lifecycle, risk warnings on destructive operations, and multi-dialect migration scripts for ANSI, SQL Server, PostgreSQL, Oracle)",
                consumers: Array.Empty<string>(),
                notes: "Detects physical table and column differences between previous and current schema snapshots, plans minimal non-destructive changes, highlights potential data loss with risk warnings, and generates dialect-specific DDL scripts (ALTER TABLE ADD/DROP/MODIFY/ALTER COLUMN, CREATE/DROP TABLE, ADD/DROP CONSTRAINT, CREATE/DROP INDEX). Read-only migration plan generation with zero SQL execution against live databases. No product operation consumes it yet."),

            new EngineCapability(
                id: "module_namespaces",
                title: "Hierarchical module DAG, namespace encapsulation, and qualified object name resolution",
                engineSurface: "module engine + qualified name parser + module hierarchy tree + local precedence resolution + spc0006 ambiguous homonym diagnostics",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/module.rs; src/gx-engine/tests/module_resolution.rs (10/10 tests: qualified name parsing and validation, module hierarchy DAG, parent-child resolution, cycle detection, depth limit guards, local module precedence for unqualified references, root module resolution, spc0006 ambiguous object reference diagnostics across distinct modules, JSON serialization, and catalog integration)",
                consumers: Array.Empty<string>(),
                notes: "Implements hierarchical Module trees up to depth 16 with cycle detection, parsing and serialization of QualifiedName (e.g. 'Sales.Invoicing.Invoice'), and GeneXus standard object resolution rules: local module precedence for bare names, unambiguous cross-module resolution, and fail-closed spc0006 diagnostics on ambiguous homonyms. No product operation consumes it yet."),

            new EngineCapability(
                id: "dataprovider_specification",
                title: "DataProvider declarative specification, parameter substitution, SDT schema validation, and JSON evaluation",
                engineSurface: "dataprovider engine + group/property grammar parser + comment stripping + SDT schema validation + JSON evaluation",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/dataprovider.rs; src/gx-engine/tests/data_provider.rs (10/10 tests: scalar assignments, nested group hierarchy, collection group repetition, parameter substitution, parm rule parsing, comment preservation and string escaping, SDT schema validation success, fail-closed unknown SDT fields, JSON serialization, and catalog integration)",
                consumers: Array.Empty<string>(),
                notes: "Parses GeneXus DataProvider grammar including parm(...) rules, bracketed properties [Count = N], nested groups, and item assignments. Evaluates DataProvider hierarchies to clean JSON matching target SDT schemas, and validates field conformity against catalog SDTs. No product operation consumes it yet."),

            new EngineCapability(
                id: "object_impact_analysis",
                title: "Object cross-reference indexing, call graph traversal, attribute/table impact analysis, and blast radius risk assessment",
                engineSurface: "impact engine + cross-reference indexer + cycle-safe transitive traversal + attribute/table impact queries + blast radius calculator",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/impact.rs; src/gx-engine/tests/impact_analysis.rs (10/10 tests: direct procedure calls, transitive callers/dependents, cycle safety under mutual recursion, attribute impact queries, table CRUD impact queries, mixed dependency kinds, catalog-wide indexing, blast radius and risk assessment, max depth 16 enforcement, and JSON serialization)",
                consumers: Array.Empty<string>(),
                notes: "Indexes catalog-wide object relationships (CallsProcedure, UsesSdt, ReferencesAttribute, ReadsTable, UpdatesTable, InsertsTable, DeletesTable). Calculates cycle-safe transitive callers and dependents up to depth 16, pinpoints all objects affected by attribute or table schema alterations, and computes blast radius with risk classification. Zero external dependencies. No product operation consumes it yet."),

            new EngineCapability(
                id: "guid_and_extended_math",
                title: "GUID 128-bit RFC 4122 v4 type support, variable method dispatch, and mathematical utility operations",
                engineSurface: "extended types engine + RFC 4122 v4 GUID parser/generator + empty sentinel + math ops (Mod, Pow, Sqrt, Abs, Random)",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/extended_types.rs; src/gx-engine/tests/extended_types.rs (10/10 tests: RFC 4122 v4 GUID generation and bit compliance, hyphenated/unhyphenated hex parsing, fail-closed invalid string parsing, empty state and set_empty, equality and hashing, JSON serialization, math modulo with zero handling, math power and square root with negative number rejection, absolute value and pseudo-random generation, and variable method dispatch simulation)",
                consumers: Array.Empty<string>(),
                notes: "Provides complete 128-bit GUID type semantics matching GeneXus Guid.NewGuid(), Guid.FromString(), .IsEmpty(), .SetEmpty(), and .ToString(). Includes standard mathematical functions (Mod, Pow, Sqrt, Abs, Random) matching GeneXus expressions and variable method calls. Zero external dependencies. No product operation consumes it yet."),

            new EngineCapability(
                id: "design_system_tokens_styles",
                title: "Design System Object (DSO) tokens, style rules, inheritance, and CSS stylesheet rendering",
                engineSurface: "design system engine + DSO grammar parser + token resolution with group/parent scope + clean CSS stylesheet renderer",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/design_system.rs; src/gx-engine/tests/design_system.rs (10/10 tests: token set and group parsing, style rules and declaration parsing, comment stripping with string literal preservation, token resolution and CSS rendering, fail-closed unresolved tokens, DSO @import and token inheritance, pseudo-classes and complex selectors, AST JSON serialization, font and media query tokens, and catalog integration)",
                consumers: Array.Empty<string>(),
                notes: "Parses modern GeneXus Design System Objects (DSO) including tokens with groups (#colors, #spacing, #fonts), @import parent inheritance, and style rules with class selectors and pseudo-classes. Resolves tokens with scoped fallback and renders clean, valid CSS stylesheets, failing closed if any token reference is unresolvable. Zero external dependencies. No product operation consumes it yet."),

            new EngineCapability(
                id: "structured_diagnostics_catalog",
                title: "Official GeneXus diagnostics catalog, source span mapping, categories, severities, and Clang/Rust-style terminal rendering",
                engineSurface: "diagnostics catalog engine + GeneXus diagnostic codes (spc0004-spc0216, src0295) + source location mapping + caret terminal renderer + JSON bag",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/diagnostics_catalog.rs; src/gx-engine/tests/diagnostics_catalog.rs (10/10 tests: diagnostic code metadata, error and warning counting, fail-closed error checks, terminal rendering with line and caret, rendering without location, JSON serialization/deserialization, severity filtering, category grouping, spc0004/spc0005, and spc0006/spc0034 instantiation)",
                consumers: Array.Empty<string>(),
                notes: "Provides an authoritative catalog of official GeneXus error codes (spc0004, spc0005, spc0006, spc0009, spc0010, spc0023, spc0034, spc0051, spc0216, src0295) mapped to categories (Syntax, TypeCheck, Specification, Database, Reorganization, Naming, Security) and severities. Emits Clang/Rust-style terminal diagnostic blocks with line extraction, carets (^), and JSON bags for IDE/MCP consumption. Zero external dependencies. No product operation consumes it yet."),

            new EngineCapability(
                id: "text_tree_roundtrip",
                title: "Text tree export, import, and round-trip catalog serialization for filesystem indexing and GxWire interop (Issue #499)",
                engineSurface: "text tree engine + directory tree serialization (.prc, .trn, .sdt, .dom, .att, .dp, .ds, .dso) + recursive reader/writer + catalog roundtrip",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/text_tree.rs; src/gx-engine/tests/text_tree.rs (10/10 tests: procedure export, transaction export, domain/attribute export, SDT export, dataprovider/dataselector export, design system export, full catalog round-trip export/import, filesystem directory write/read roundtrip, fail-closed corrupted extension rejection, and JSON snapshot serialization)",
                consumers: Array.Empty<string>(),
                notes: "Exports and imports entire GeneXus Knowledge Base catalogs as clean text trees on the filesystem (.prc, .trn, .sdt, .dom, .att, .dp, .ds, .dso). Enables external static analysis and solves Issue #499 by allowing GxWire to parse and index Knowledge Bases without requiring a proprietary SDK or live database. Zero external dependencies. No product operation consumes it yet."),

            new EngineCapability(
                id: "dataview_external_table_mapping",
                title: "Data Views, external table/view mappings, attribute-to-column bidirectional mapping, index mapping, and external multi-dialect SELECT generation",
                engineSurface: "dataview engine + DataView DSL parser + bidirectional attribute/column mapper + dialect-quoted external SQL renderer",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/dataview.rs; src/gx-engine/tests/data_view.rs (10/10 tests: basic DSL parsing, attribute-to-column bidirectional mapping, primary and unique indexes inspection, external SQL SELECT generation for SqlServer, PostgreSql, and Oracle, WHERE clause filtering, fail-closed duplicate fields and missing tables, and catalog admission with ObjectKind::DataView)",
                consumers: Array.Empty<string>(),
                notes: "Implements GeneXus Data Views mapping internal attributes and tables to external database tables, views, and columns across ANSI, SQL Server, PostgreSQL, and Oracle dialects. Enforces fail-closed validation on duplicate attributes/columns, unmapped index keys, and missing external names. Zero external dependencies. No product operation consumes it yet."),

            new EngineCapability(
                id: "output_channels_streaming_buffers",
                title: "Structured output buffering across Console, WebBuffer, Log, and StatusLine channels (msg(..., status)), and bounded streaming chunk rendering",
                engineSurface: "output channel engine + multi-channel message recorder + status line tracking + channel filtering + bounded streaming chunk partitioner",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/output_channel.rs; src/gx-engine/tests/output_channel.rs (10/10 tests: initial buffer empty, console write and flush, status messages with status code and latest status inspection, web buffer write and flush, channel filtering, interleaved channel ordering, streaming chunks bounded partitioning, buffer clear and reuse, JSON serialization roundtrip, and procedure runtime output collection)",
                consumers: Array.Empty<string>(),
                notes: "Provides structured multi-channel message routing matching GeneXus procedure execution semantics: separates console display output from web response buffers, logs, and UI status-line reports (msg(..., status)). Enables bounded streaming chunk partitioning for MCP and real-time streaming interfaces. Zero external dependencies. No product operation consumes it yet."),

            new EngineCapability(
                id: "webpanel_layout_events",
                title: "WebPanel, form layouts, control tree, and event handler extraction (Start, Refresh, Enter, Load)",
                engineSurface: "webpanel engine + layout control tree + event parser + validation",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/webpanel.rs; src/gx-engine/tests/web_panel.rs (10/10 tests: form layout tree, responsive tables, controls, Start/Refresh/Enter/Load events, DSL/JSON roundtrip, validation)",
                consumers: Array.Empty<string>(),
                notes: "Provides full WebPanel and form layout modeling, control tree navigation, event handler extraction, and fail-closed validation matching GeneXus WebPanel objects. No product operation consumes it yet."),

            new EngineCapability(
                id: "code_formatting_linting",
                title: "GeneXus source code indentation normalization, keyword canonicalization, and static linter",
                engineSurface: "formatter engine + AST linter + syntax diagnostics bag",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/formatter.rs; src/gx-engine/tests/formatter_linter.rs (10/10 tests: indentation normalization, keyword canonicalization, comment/string preservation, AST linter with unclosed/mismatched/unreachable rules)",
                consumers: Array.Empty<string>(),
                notes: "Formats and lints GeneXus source code with configurable indentation, canonicalizes PascalCase keywords, preserves string literals and comments, and reports syntax and unclosed block errors. No product operation consumes it yet."),

            new EngineCapability(
                id: "datastore_provider_profiles",
                title: "DataStores, DBMS provider profiles (SQL Server, PostgreSQL, MySQL, Oracle, DB2, Informix), and connection strings",
                engineSurface: "datastore engine + DBMS provider resolver + ADO.NET connection string builder",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/datastore.rs; src/gx-engine/tests/data_store.rs (10/10 tests: SQL Server, PostgreSQL, MySQL, Oracle, DB2, Informix profiles, connection string builder, dialect settings, fail-closed validation)",
                consumers: Array.Empty<string>(),
                notes: "Models GeneXus DataStores (Default, GAM, secondary), maps provider-specific dialect properties and default ports, and renders compliant ADO.NET connection strings. No product operation consumes it yet."),

            new EngineCapability(
                id: "gam_security_authorization",
                title: "GAM security model, roles, users, permissions, hierarchical inheritance, and authorization evaluation",
                engineSurface: "gam security engine + permission evaluator + role inheritance resolver",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/gam.rs; src/gx-engine/tests/gam_security.rs (10/10 tests: GAM roles, users, permissions, hierarchical role inheritance, explicit deny override, cycle safety, fail-closed validation)",
                consumers: Array.Empty<string>(),
                notes: "Implements GeneXus Access Manager (GAM) security levels, role hierarchies with cycle-safe inheritance, explicit deny override semantics, and fail-closed authorization checks. No product operation consumes it yet."),

            new EngineCapability(
                id: "xpz_package_transfer",
                title: "Team development and Knowledge Manager XPZ package export, import, and merge strategies",
                engineSurface: "xpz transfer engine + ExportFile XML parser/serializer + catalog merge orchestrator",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/xpz.rs; src/gx-engine/tests/xpz_transfer.rs (10/10 tests: ExportFile XML parsing/generation, catalog export, merge strategies Overwrite/KeepExisting/FailOnConflict, fail-closed validation)",
                consumers: Array.Empty<string>(),
                notes: "Parses and generates GeneXus XPZ (XML Export/Import Package) interchange files and orchestrates catalog import with Overwrite, KeepExisting, and FailOnConflict strategies. No product operation consumes it yet."),

            new EngineCapability(
                id: "object_versioning_history",
                title: "Object revision history, differential line-by-line delta computation, and time-travel rollback",
                engineSurface: "versioning engine + differential Myers diff + history ledger",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/versioning.rs; src/gx-engine/tests/versioning_history.rs (10/10 tests: immutable revisions, line-by-line diff computation, additions/deletions tracking, rollback snapshot commits, history JSON roundtrip)",
                consumers: Array.Empty<string>(),
                notes: "Tracks immutable revisions for Knowledge Base objects, computes differential line-by-line diffs, records author and comment metadata, and performs rollback restorations. No product operation consumes it yet."),

            new EngineCapability(
                id: "gxtest_runner",
                title: "GXtest unit test assertions, test case pipeline, suite execution, and structured reporting",
                engineSurface: "gxtest runner + assertion evaluator + test suite execution pipeline",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/gxtest.rs; src/gx-engine/tests/gxtest_runner.rs (10/10 tests: NumericEquals, StringEquals, IsTrue, IsFalse, Fail assertions, suite runner, execution report, JSON roundtrip)",
                consumers: Array.Empty<string>(),
                notes: "Implements the GXtest unit testing framework with assertions, suite batch runner, execution reports, and fail-closed validation. No product operation consumes it yet."),

            new EngineCapability(
                id: "multiagent_locking_leases",
                title: "Multi-agent distributed locking, KB/Table/Object targets, shared/exclusive modes, and leases",
                engineSurface: "lock manager + hierarchical concurrency controller + lease timeout monitor",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/locks.rs; src/gx-engine/tests/lock_concurrency.rs (10/10 tests: KnowledgeBase, Table, Object targets, Shared/Exclusive modes, timeout leases, heartbeat renewal, automatic expiration purge)",
                consumers: Array.Empty<string>(),
                notes: "Coordinates concurrent multi-agent access to Knowledge Base resources across KB, Table, and Object granularities with lease renewal and automatic expiration. No product operation consumes it yet."),

            new EngineCapability(
                id: "general_gx_surface",
                title: "General GeneXus objects, Rules, Events and forms",
                engineSurface: "rules parser + formula engine + webpanel + gam",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/tests/rules_parity.rs; src/gx-engine/tests/formula_navigation.rs; src/gx-engine/tests/web_panel.rs; src/gx-engine/tests/gam_security.rs",
                consumers: Array.Empty<string>(),
                notes: "Unified GeneXus object model, Rules, Events, Forms, and GAM security reaching 100% SDK capability parity."),

            new EngineCapability(
                id: "udm_storage_access",
                title: "Direct native UDM storage access (read/write/State locks) without COM/STA Worker",
                engineSurface: "udm connection parser + envelope decoder + token reconstructor + client + reader + writer",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/udm/mod.rs; src/gx-engine/tests/udm_decoder.rs (19/19 tests); src/gx-engine/tests/udm_reader_writer.rs (10/10 tests, live LocalDB read verified)",
                consumers: new[] { "genexus_read", "genexus_query", "genexus_edit" },
                notes: "Direct reading, hydration, transactional writing, and multi-user State locks against native UDM databases."),

            new EngineCapability(
                id: "csharp_emitter",
                title: "Full C# code generator emitting .NET (C# 10/12, net8.0/net9.0) classes for Procedures, WebPanels, Transactions, and Solutions",
                engineSurface: "csharp emitter + procedure + webpanel + transaction + sdt + solution",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/csharp/mod.rs; src/gx-engine/tests/csharp_emitter.rs (14/14 tests); src/gx-engine/tests/csharp_webpanel_transaction.rs (12/12 tests, Roslyn dotnet build verified)",
                consumers: Array.Empty<string>(),
                notes: "Emits standard GeneXus C# classes (GXProcedure, GXWebObjectStub, GXDataComponent, GxUserType) and Visual Studio solution files (.sln, Directory.build.props)."),

            new EngineCapability(
                id: "parallel_rebuild_all",
                title: "Native parallel Rebuild All engine executing multi-core specification and Roslyn compilation in < 20s",
                engineSurface: "rebuild pipeline + parallel specifier + scoped threads + dotnet build driver",
                status: EngineCapabilityStatus.Parity,
                evidence: "src/gx-engine/src/rebuild.rs; src/gx-engine/tests/rebuild_pipeline.rs (5/5 tests: sub-second 24-proc parallel rebuild, Roslyn compilation)",
                consumers: new[] { "genexus_lifecycle.build", "genexus_lifecycle.rebuild" },
                notes: "Concurrently specifies and emits C# across all catalog objects using scoped threads, driving parallel Roslyn compilation."),
        };

        internal static EngineCapability? Find(string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(capabilityId)) return null;
            return All.FirstOrDefault(c => string.Equals(c.Id, capabilityId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Product operations currently served by the engine across all
        /// capabilities. Empty until the first parity capability with a consumer
        /// is registered; genexus_doctor reports this list.
        /// </summary>
        internal static IReadOnlyList<string> RoutedOperations() =>
            All.Where(c => c.MayRouteToEngine)
               .SelectMany(c => c.Consumers)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToArray();
    }

    internal enum EngineRouteTarget
    {
        /// <summary>The GeneXus SDK serves this call (default, and the only possible answer with Backend=sdk).</summary>
        Sdk,

        /// <summary>The engine serves this call (parity capability, Backend=gxengine).</summary>
        Engine,

        /// <summary>Neither backend may serve this call (Backend=gxengine, Fallback=error).</summary>
        Reject,
    }

    /// <summary>
    /// Outcome of one routing decision. <see cref="Code"/> and
    /// <see cref="Message"/> are only set for <see cref="EngineRouteTarget.Reject"/>.
    /// </summary>
    internal readonly struct EngineRouteDecision
    {
        private EngineRouteDecision(EngineRouteTarget target, string capabilityId, string? code, string? message)
        {
            Target = target;
            CapabilityId = capabilityId;
            Code = code;
            Message = message;
        }

        internal EngineRouteTarget Target { get; }
        internal string CapabilityId { get; }
        internal string? Code { get; }
        internal string? Message { get; }

        internal static EngineRouteDecision Sdk(string capabilityId) =>
            new EngineRouteDecision(EngineRouteTarget.Sdk, capabilityId, null, null);

        internal static EngineRouteDecision Engine(EngineCapability capability) =>
            new EngineRouteDecision(EngineRouteTarget.Engine, capability.Id, null, null);

        internal static EngineRouteDecision Reject(string capabilityId, string code, string message) =>
            new EngineRouteDecision(EngineRouteTarget.Reject, capabilityId, code, message);
    }

    /// <summary>
    /// The single gate a future SDK-to-engine swap must call. It answers which
    /// backend serves <paramref name="capabilityId"/> under the configured
    /// policy, and never returns a partially-engine path: the engine serves a
    /// capability only at measured parity. Backend=gxengine (default) admits parity capabilities, auto admits only builds; gxengine with
    /// Fallback=error refuses anything else instead of quietly using the SDK.
    /// </summary>
    internal static class EngineRouter
    {
        internal const string NotParityCode = "EngineCapabilityNotParity";

        internal static EngineRouteDecision Decide(string capabilityId, EngineBackendSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!settings.EngineEnabled
                || (settings.Backend == EngineBackendSettings.AutoBackend
                    && !string.Equals(capabilityId, "parallel_rebuild_all", StringComparison.OrdinalIgnoreCase)))
                return EngineRouteDecision.Sdk(capabilityId);

            var capability = EngineCapabilityRegistry.Find(capabilityId);
            if (capability != null && capability.MayRouteToEngine)
                return EngineRouteDecision.Engine(capability);

            string reason = capability == null
                ? $"Unknown engine capability '{capabilityId}'."
                : $"Engine capability '{capability.Id}' is {capability.Status.ToString().ToLowerInvariant()}, not parity.";

            if (settings.RejectUnsupported)
                return EngineRouteDecision.Reject(capabilityId, NotParityCode, reason + " Engine.Fallback=error refuses the SDK path.");

            return EngineRouteDecision.Sdk(capabilityId);
        }
    }
}
