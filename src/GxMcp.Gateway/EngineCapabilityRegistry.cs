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
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/README.md (portable project format); src/gx-engine/tests/decimal_oracle.rs, typed_values.rs",
                consumers: Array.Empty<string>(),
                notes: "Not a GeneXus Numeric/Decimal/null compatibility claim."),

            new EngineCapability(
                id: "text_core",
                title: "Bounded text operations (Len, Trim, comparisons) over CP1252 bytes",
                engineSurface: "portable Procedure runtime",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/tests/len_builtin.rs, text_values.rs; high-byte CP1252 divergence recorded in plans/129-implementation-progress.md",
                consumers: Array.Empty<string>(),
                notes: "Full string-library collation semantics are not implemented."),

            new EngineCapability(
                id: "calendar_values",
                title: "Date/DateTime component and literal subset",
                engineSurface: "portable Procedure runtime",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/tests/date_values.rs, date_pipeline.rs, datetime_values.rs",
                consumers: Array.Empty<string>(),
                notes: "No timezone, DST or fractional-second coverage claim."),

            new EngineCapability(
                id: "records_collections",
                title: "Bounded record/collection runtime with native-measured aliasing",
                engineSurface: "portable format-2 compiler and runtime",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/README.md (bounded records and collections); src/gx-engine/tests/record_runtime.rs, collection_indices.rs",
                consumers: Array.Empty<string>(),
                notes: "Invalid collection index is a controlled engine error where native crashes (deliberate safety divergence)."),

            new EngineCapability(
                id: "procedure_pipeline",
                title: "Admitted Procedure compile and run (control flow, calls, Parm modes)",
                engineSurface: "gx-engine check/build/run over the portable catalog",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/tests/typed_pipeline.rs, do_case.rs, for_loop.rs, parm_modes.rs; fixtures/catalog-demo.json",
                consumers: Array.Empty<string>(),
                notes: "Bounded reachability (1,024 Procedures); no Rules/Events beyond standalone Parm."),

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
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/README.md (read-only SDK XML compilation projection); exchange suite 16/16; domain_attribute suite 10/10; fresh disposable-KB exports run unchanged",
                consumers: Array.Empty<string>(),
                notes: "Bounded depth and item-alias rules; a compilation projection, not a lossless importer."),

            new EngineCapability(
                id: "kb_catalog",
                title: "Portable KB catalog load, validate and edit",
                engineSurface: "gx-engine kb-check/kb-emit/kb-build/kb-run",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/README.md (independent portable KB catalog); src/gx-engine/tests/kb_pipeline.rs, catalog.rs",
                consumers: Array.Empty<string>(),
                notes: "The catalog format is the engine's own, not a native KB reader; native access is native_kb_read."),

            new EngineCapability(
                id: "native_kb_read",
                title: "Native GeneXus Knowledge Base database read",
                engineSurface: "none",
                status: EngineCapabilityStatus.Unsupported,
                evidence: "plans/129-sdk-capability-matrix.md (native-KB read rows, 0% covered)",
                consumers: Array.Empty<string>(),
                notes: "No native KB decoder exists; every native read must stay on the SDK."),

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
                id: "general_gx_surface",
                title: "General GeneXus objects, Rules, Events and forms",
                engineSurface: "none",
                status: EngineCapabilityStatus.Unsupported,
                evidence: "src/gx-engine/README.md (unsupported features list)",
                consumers: Array.Empty<string>(),
                notes: "Anything outside the admitted Procedure/SDT subset fails closed by design."),
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
    /// capability only at measured parity, and Backend=gxengine with
    /// Fallback=error refuses anything else instead of quietly using the SDK.
    /// </summary>
    internal static class EngineRouter
    {
        internal const string NotParityCode = "EngineCapabilityNotParity";

        internal static EngineRouteDecision Decide(string capabilityId, EngineBackendSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!settings.EngineEnabled)
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
