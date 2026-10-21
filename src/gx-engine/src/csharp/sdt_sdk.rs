//! The SDK's form of a Structured Data Type: one `type_Sdt<class>.cs` per class (the SDT and each of its
//! levels), as GeneXus 18 writes it with `Packages/Templates/CSharpWeb/SDTs.stg` (Json.stg, Properties.stg,
//! Rest.stg).
//!
//! The text, blank lines included, is matched against the SDK's files on KBTeste
//! (`type_SdtRustFreshSdt091817`, `RST54161700`, `LiveSdtMember60744600`, `RSC79223900`, `RCE06829400`,
//! `RSN03944700`, `NativeDeepSdt85299100` and their level classes: 13 files, byte for byte). Only the
//! shapes those files show are written; any other SDT is refused here, and the caller keeps the portable
//! class (`SdtClass::emit`). The covered members: Numeric without decimals (`short`, `int`), Character,
//! VarChar, Boolean, Date and DateTime fields; nested levels and collection levels, each after the fields
//! of its class. A record root only: a collection root has no class of its own.

use crate::{csharp::sdt::SdtClass, model::DataType, udm::stored_sdt::SdtNode};

/// The header the SDK writes on every file, after the class name and description.
const AUTHOR: &str = "\t\t\t\t Author: Nemo \u{1F420} for C# (.NET) version 18.0.10.184260\n\t\t   Program type: Callable routine\n\t\t\t  Main DBMS: \n*/\n";

const USINGS: &str = "using System;\nusing System.Collections;\nusing GeneXus.Utils;\nusing GeneXus.Resources;\nusing GeneXus.Application;\nusing GeneXus.Metadata;\nusing GeneXus.Cryptography;\nusing GeneXus.Encryption;\nusing GeneXus.Http.Client;\nusing GeneXus.Http.Server;\nusing System.Reflection;\nusing System.Xml.Serialization;\nusing System.Runtime.Serialization;\n\n\n";

/// What a member is, for the template's choices and for the blank lines between members.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    Num,
    Char,
    Bool,
    Date,
    DateTime,
    Level,
    Collection,
}

impl Kind {
    fn is_level(self) -> bool {
        matches!(self, Kind::Level | Kind::Collection)
    }
}

/// One member of a class, with the names the template writes.
struct Member<'a> {
    node: &'a SdtNode,
    kind: Kind,
    /// The stored name (`JsonName`, `ElementName`).
    name: &'a str,
    /// The first letter upper-cased, the rest lower-cased (`NormalizedName`).
    normalized: String,
    /// `gxTpr_<normalized>`.
    property: String,
    /// `gxTv_<class>_<normalized>`.
    field: String,
}

/// One class the SDK writes for an SDT: the SDT itself or one of its levels.
struct Class<'a> {
    name: String,
    /// The dotted GeneXus name (`RSN03944700.RowsItem`).
    dotted: String,
    description: String,
    nodes: &'a [SdtNode],
    /// A collection item: its REST class is `wrapped`.
    item: bool,
}

/// The files of an SDT in the SDK's form, as (file name, text). An SDT outside the evidenced shapes is an
/// `Err` naming why; the caller then writes the portable class.
pub fn sdk_files(sdt: &SdtClass) -> Result<Vec<(String, String)>, String> {
    if sdt.root_collection {
        return Err("a collection root is not a shape the SDK evidence covers".into());
    }
    if !sdt.name.is_ascii() || !sdt.nodes.iter().all(ascii_names) {
        return Err("a name outside ASCII is not a shape the SDK evidence covers".into());
    }
    let xml_namespace = sdt
        .xml_namespace
        .as_deref()
        .ok_or("the SDT's XML namespace was not read")?;
    let mut classes = Vec::new();
    collect(
        &mut classes,
        sdt.class_name(),
        sdt.sdk_name.clone(),
        sdt.name.clone(),
        &sdt.nodes,
        false,
    );
    classes
        .iter()
        .map(|class| {
            emit(class, &sdt.namespace, xml_namespace)
                .map(|text| (format!("type_{}.cs", class.name), text))
        })
        .collect()
}

fn collect<'a>(
    out: &mut Vec<Class<'a>>,
    name: String,
    dotted: String,
    description: String,
    nodes: &'a [SdtNode],
    item: bool,
) {
    out.push(Class {
        name: name.clone(),
        dotted: dotted.clone(),
        description,
        nodes,
        item,
    });
    for node in nodes {
        if let SdtNode::Level {
            name: level,
            collection,
            children,
            ..
        } = node
        {
            collect(
                out,
                child_class(&name, node),
                format!("{dotted}.{}", child_suffix(node)),
                level.clone(),
                children,
                *collection,
            );
        }
    }
}

fn ascii_names(node: &SdtNode) -> bool {
    match node {
        SdtNode::Field { name, .. } => name.is_ascii(),
        SdtNode::Level {
            name,
            item_name,
            children,
            ..
        } => {
            name.is_ascii()
                && item_name.as_deref().is_none_or(str::is_ascii)
                && children.iter().all(ascii_names)
        }
    }
}

/// The class suffix of a level: its name, or for a collection its item name (`<level>Item` when none).
fn child_suffix(node: &SdtNode) -> String {
    match node {
        SdtNode::Level {
            name,
            collection: true,
            item_name,
            ..
        } => item_name.clone().unwrap_or_else(|| format!("{name}Item")),
        SdtNode::Level { name, .. } | SdtNode::Field { name, .. } => name.clone(),
    }
}

fn child_class(holder: &str, node: &SdtNode) -> String {
    format!("{holder}_{}", child_suffix(node))
}

fn normalized(name: &str) -> String {
    let mut chars = name.chars();
    match chars.next() {
        None => String::new(),
        Some(first) => format!(
            "{}{}",
            first.to_ascii_uppercase(),
            chars.as_str().to_lowercase()
        ),
    }
}

/// The C# type of a covered field.
fn cs_type(data_type: &DataType) -> &'static str {
    match data_type {
        DataType::Numeric { precision, .. } if *precision <= 4 => "short",
        DataType::Numeric { .. } => "int",
        DataType::Boolean {} => "bool",
        DataType::Date {} | DataType::DateTime {} => "DateTime",
        _ => "string",
    }
}

fn kind_of(node: &SdtNode) -> Result<Kind, String> {
    match node {
        SdtNode::Level {
            collection: true, ..
        } => Ok(Kind::Collection),
        SdtNode::Level { .. } => Ok(Kind::Level),
        SdtNode::Field { name, data_type } => match data_type {
            DataType::Numeric {
                precision, scale, ..
            } if *scale == 0 && *precision <= 9 => Ok(Kind::Num),
            DataType::Character { .. } | DataType::Varchar { .. } => Ok(Kind::Char),
            DataType::Boolean {} => Ok(Kind::Bool),
            DataType::Date {} => Ok(Kind::Date),
            DataType::DateTime {} => Ok(Kind::DateTime),
            other => Err(format!(
                "member '{name}' of type {other:?} is not a shape the SDK evidence covers"
            )),
        },
    }
}

/// The members, after checking each is covered and that no field follows a level (the blank lines
/// between a level and a following field are not evidenced).
fn members<'a>(class: &Class<'a>) -> Result<Vec<Member<'a>>, String> {
    let mut out: Vec<Member<'a>> = Vec::new();
    for node in class.nodes {
        let kind = kind_of(node)?;
        let name = match node {
            SdtNode::Field { name, .. } | SdtNode::Level { name, .. } => name.as_str(),
        };
        if !kind.is_level() && out.iter().any(|m| m.kind.is_level()) {
            return Err(format!(
                "field '{name}' follows a level in '{}', which the SDK evidence does not cover",
                class.dotted
            ));
        }
        let normalized = normalized(name);
        out.push(Member {
            node,
            kind,
            name,
            property: format!("gxTpr_{normalized}"),
            field: format!("gxTv_{}_{normalized}", class.name),
            normalized,
        });
    }
    if out.is_empty() {
        return Err(format!("'{}' has no members", class.dotted));
    }
    Ok(out)
}

/// The members' texts joined: `join(previous, next)` between two, `end(last)` after the last.
fn joined(
    out: &mut String,
    items: &[(Kind, String)],
    join: impl Fn(Kind, Kind) -> usize,
    end: impl Fn(Kind) -> usize,
) {
    for (index, (kind, text)) in items.iter().enumerate() {
        out.push_str(text);
        let newlines = match items.get(index + 1) {
            Some((next, _)) => join(*kind, *next),
            None => end(*kind),
        };
        out.push_str(&"\n".repeat(newlines));
    }
}

fn data_type<'a>(member: &Member<'a>) -> Option<&'a DataType> {
    match member.node {
        SdtNode::Field { data_type, .. } => Some(data_type),
        SdtNode::Level { .. } => None,
    }
}

fn emit(class: &Class<'_>, namespace: &str, xml_namespace: &str) -> Result<String, String> {
    let members = members(class)?;
    let cls = &class.name;
    let has_date = members
        .iter()
        .any(|m| matches!(m.kind, Kind::Date | Kind::DateTime));
    let has_datetime = members.iter().any(|m| m.kind == Kind::DateTime);
    let mut o = String::new();
    o.push_str(&format!(
        "/*\n\t\t\t\t   File: type_{cls}\n\t\t\tDescription: {}\n{AUTHOR}",
        class.description
    ));
    o.push_str(USINGS);
    o.push_str(&format!("namespace {namespace}\n{{\n"));
    o.push_str(&format!(
        "\t[XmlRoot(ElementName=\"{d}\")]\n\t[XmlType(TypeName=\"{d}\" , Namespace=\"{xml_namespace}\" )]\n\t[Serializable]\n",
        d = class.dotted
    ));
    o.push_str(&format!("\tpublic class {cls} : GxUserType\n\t{{\n"));

    // Constructors: Character and DateTime fields are set in the serialization constructor.
    o.push_str(&format!(
        "\t\tpublic {cls}( )\n\t\t{{\n\t\t\t/* Constructor for serialization */\n"
    ));
    for m in &members {
        match m.kind {
            Kind::Char => o.push_str(&format!("\t\t\t{} = \"\";\n\n", m.field)),
            Kind::DateTime => o.push_str(&format!(
                "\t\t\t{} = (DateTime)(DateTime.MinValue);\n\n",
                m.field
            )),
            _ => {}
        }
    }
    o.push_str(&format!(
        "\t\t}}\n\n\t\tpublic {cls}(IGxContext context)\n\t\t{{\n\t\t\tthis.context = context;\t\n\t\t\tinitialize();\n\t\t}}\n\n"
    ));

    // Json.
    o.push_str("\t\t#region Json\n\t\tprivate static Hashtable mapper;\n\t\tpublic override string JsonMap(string value)\n\t\t{\n\t\t\tif (mapper == null)\n\t\t\t{\n\t\t\t\tmapper = new Hashtable();\n\t\t\t}\n\t\t\treturn (string)mapper[value]; ;\n\t\t}\n\n");
    o.push_str("\t\tpublic override void ToJSON()\n\t\t{\n\t\t\tToJSON(true) ;\n\t\t\treturn;\n\t\t}\n\n\t\tpublic override void ToJSON(bool includeState)\n\t\t{\n");
    let items: Vec<(Kind, String)> = members.iter().map(|m| (m.kind, json_item(m))).collect();
    let json_trail = |k: Kind| match k {
        Kind::Level | Kind::Collection => 0,
        Kind::Date | Kind::DateTime => 2,
        _ => 1,
    };
    joined(
        &mut o,
        &items,
        |a, b| json_trail(a) + usize::from(!b.is_level()),
        json_trail,
    );
    o.push_str("\t\t\treturn;\n\t\t}\n\t\t#endregion\n\n\t\t#region Properties\n\n");

    // Properties.
    let items: Vec<(Kind, String)> = members
        .iter()
        .map(|m| (m.kind, property_item(cls, &class.dotted, m)))
        .collect();
    let property_trail = |k: Kind| match k {
        Kind::Num | Kind::Char => 3,
        Kind::Date => 1,
        _ => 2,
    };
    joined(
        &mut o,
        &items,
        |a, b| property_trail(a) + usize::from(b != Kind::Level),
        property_trail,
    );
    o.push_str("\t\tpublic override bool ShouldSerializeSdtJson()\n\t\t{\n\t\t\treturn true;\n\t\t}\n\n\n\n\t\t#endregion\n\n");
    o.push_str("\t\t#region Static Type Properties\n\n\t\t[XmlIgnore]\n\t\tprivate static GXTypeInfo _typeProps;\n\t\tprotected override GXTypeInfo TypeInfo { get { return _typeProps; } set { _typeProps = value; } }\n\n\t\t#endregion\n\n");

    // Initialization: a Numeric field writes nothing; Boolean and Date write an empty line.
    o.push_str("\t\t#region Initialization\n\n\t\tpublic void initialize( )\n\t\t{\n");
    let items: Vec<(Kind, String)> = members
        .iter()
        .filter(|m| m.kind != Kind::Num)
        .map(|m| {
            let text = match m.kind {
                Kind::Char => format!("\t\t\t{} = \"\";", m.field),
                Kind::DateTime => format!("\t\t\t{} = (DateTime)(DateTime.MinValue);", m.field),
                Kind::Level | Kind::Collection => format!("\t\t\t{}_N = true;\n", m.field),
                _ => String::new(),
            };
            (m.kind, text)
        })
        .collect();
    joined(
        &mut o,
        &items,
        |_, b| if b.is_level() { 2 } else { 1 },
        |_| 1,
    );
    if has_datetime {
        o.push_str("\t\t\tdatetime_STZ = (DateTime)(DateTime.MinValue);\n");
    }
    if has_date {
        o.push_str("\t\t\tsDateCnv = \"\";\n\t\t\tsNumToPad = \"\";\n");
    }
    o.push_str("\t\t\treturn  ;\n\t\t}\n\n\n\n\t\t#endregion\n\n\t\t#region Declaration\n\n");

    // Declaration.
    if has_date {
        o.push_str("\t\tprotected string sDateCnv ;\n\t\tprotected string sNumToPad ;\n");
        if has_datetime {
            o.push_str("\t\tprotected DateTime datetime_STZ ;\n");
        }
        o.push('\n');
    }
    let items: Vec<(Kind, String)> = members
        .iter()
        .map(|m| {
            let text = match m.kind {
                Kind::Level => format!(
                    "\t\tprotected bool {f}_N;\n\t\tprotected {c} {f} = null; \n",
                    f = m.field,
                    c = child_class(cls, m.node)
                ),
                Kind::Collection => format!(
                    "\t\tprotected bool {f}_N;\n\t\tprotected GXBaseCollection<{c}> {f} = null; \n",
                    f = m.field,
                    c = child_class(cls, m.node)
                ),
                _ => format!(
                    "\t\tprotected {} {};\n\t\t \n",
                    cs_type(data_type(m).expect("a field")),
                    m.field
                ),
            };
            (m.kind, text)
        })
        .collect();
    joined(
        &mut o,
        &items,
        |a, b| usize::from(a.is_level() || !b.is_level()),
        |a| if a.is_level() { 3 } else { 2 },
    );
    o.push_str("\t\t#endregion\n\t}\n");

    // Rest interface.
    o.push_str(&format!(
        "\t#region Rest interface\n\t[GxJsonSerialization(\"{}\")]\n\t[DataContract(Name=@\"{}\", Namespace=\"{xml_namespace}\")]\n",
        if class.item { "wrapped" } else { "default" },
        class.dotted
    ));
    o.push_str(&format!("\tpublic class {cls}_RESTInterface : GxGenericCollectionItem<{cls}>, System.Web.SessionState.IRequiresSessionState\n\t{{\n"));
    o.push_str(&format!("\t\tpublic {cls}_RESTInterface( ) : base()\n\t\t{{\t\n\t\t}}\n\n\t\tpublic {cls}_RESTInterface( {cls} psdt ) : base(psdt)\n\t\t{{\t\n\t\t}}\n\n\t\t#region Rest Properties\n"));
    for (order, m) in members.iter().enumerate() {
        o.push_str(&rest_item(cls, m, order));
    }
    o.push_str(&format!("\n\t\t#endregion\n\n\t\tpublic {cls} sdt\n\t\t{{\n\t\t\tget {{ \n\t\t\t\treturn ({cls})Sdt;\n\t\t\t}}\n\t\t\tset {{ \n\t\t\t\tSdt = value;\n\t\t\t}}\n\t\t}}\n\n"));
    o.push_str(&format!("\t\t[OnDeserializing]\n\t\tvoid checkSdt( StreamingContext ctx )\n\t\t{{\n\t\t\tif ( sdt == null )\n\t\t\t{{\n\t\t\t\tsdt = new {cls}() ;\n\t\t\t}}\n\t\t}}\n\t}}\n\t#endregion\n}}"));
    Ok(o)
}

/// The `sDateCnv` lines of one date part: `sNumToPad` from `DateTimeUtil.<part>(<value>)`, padded.
fn date_part(
    out: &mut String,
    part: &str,
    value: &str,
    pad: &str,
    len_space: bool,
    trim_space: bool,
) {
    out.push_str(&format!(
        "\t\t\tsNumToPad = StringUtil.Trim({}StringUtil.Str((decimal)(DateTimeUtil.{part}({value})), 10, 0));\n",
        if trim_space { " " } else { "" }
    ));
    out.push_str(&format!(
        "\t\t\tsDateCnv = sDateCnv + StringUtil.Substring(\"{pad}\", 1, {}-StringUtil.Len({}sNumToPad)) + sNumToPad;\n",
        pad.len(),
        if len_space { " " } else { "" }
    ));
}

fn json_item(m: &Member<'_>) -> String {
    let (name, p) = (m.name, &m.property);
    match m.kind {
        Kind::Level | Kind::Collection => format!(
            "\t\t\tif ({f} != null)\n\t\t\t{{\n\t\t\t\tAddObjectProperty(\"{name}\", {f}, false);\n\t\t\t}}\n",
            f = m.field
        ),
        Kind::Date => {
            let mut t = "\t\t\tsDateCnv = \"\";\n".to_string();
            date_part(&mut t, "Year", p, "0000", false, false);
            t.push_str("\t\t\tsDateCnv = sDateCnv + \"-\";\n");
            date_part(&mut t, "Month", p, "00", false, false);
            t.push_str("\t\t\tsDateCnv = sDateCnv + \"-\";\n");
            date_part(&mut t, "Day", p, "00", false, false);
            t.push_str(&format!(
                "\t\t\tAddObjectProperty(\"{name}\", sDateCnv, false);\n"
            ));
            t
        }
        Kind::DateTime => {
            let mut t = format!("\t\t\tdatetime_STZ = {p};\n\t\t\tsDateCnv = \"\";\n");
            date_part(&mut t, "Year", "datetime_STZ", "0000", true, false);
            t.push_str("\t\t\tsDateCnv = sDateCnv + \"-\";\n");
            date_part(&mut t, "Month", "datetime_STZ", "00", false, true);
            t.push_str("\t\t\tsDateCnv = sDateCnv + \"-\";\n");
            for (part, separator) in [("Day", "T"), ("Hour", ":"), ("Minute", ":")] {
                date_part(&mut t, part, "datetime_STZ", "00", false, false);
                t.push_str(&format!("\t\t\tsDateCnv = sDateCnv + \"{separator}\";\n"));
            }
            date_part(&mut t, "Second", "datetime_STZ", "00", false, false);
            t.push_str(&format!(
                "\t\t\tAddObjectProperty(\"{name}\", sDateCnv, false);\n"
            ));
            t
        }
        _ => format!("\t\t\tAddObjectProperty(\"{name}\", {p}, false);\n"),
    }
}

fn property_item(cls: &str, dotted: &str, m: &Member<'_>) -> String {
    let (name, nn, p, f) = (m.name, &m.normalized, &m.property, &m.field);
    let null_members = format!(
        "\t\tpublic void {f}_SetNull()\n\t\t{{\n\t\t\t{f}_N = true;\n\t\t\t{f} = null;\n\t\t}}\n\n\t\tpublic bool {f}_IsNull()\n\t\t{{\n\t\t\treturn {f} == null;\n\t\t}}\n"
    );
    match m.kind {
        Kind::Level => {
            let c = child_class(cls, m.node);
            format!(
                "\t\t[SoapElement(ElementName=\"{name}\" )]\n\t\t[XmlElement(ElementName=\"{name}\" )]\n\t\tpublic {c} {p}\n\t\t{{\n\t\t\tget {{\n\t\t\t\tif ( {f} == null )\n\t\t\t\t{{\n\t\t\t\t\t{f} = new {c}(context);\n\t\t\t\t}}\n\t\t\t\t{f}_N = false;\n\t\t\t\treturn {f};\n\t\t\t}}\n\t\t\tset {{\n\t\t\t\t{f}_N = false;\n\t\t\t\t{f} = value;\n\t\t\t\tSetDirty(\"{nn}\");\n\t\t\t}}\n\n\t\t}}\n\n{null_members}\t\tpublic bool ShouldSerialize{p}_Json()\n\t\t{{\n\t\t\t\treturn ({f} != null && {f}.ShouldSerializeSdtJson());\n\n\t\t}}\n"
            )
        }
        Kind::Collection => {
            let c = child_class(cls, m.node);
            let item = child_suffix(m.node);
            format!(
                "\t\t[SoapElement(ElementName=\"{name}\" )]\n\t\t[XmlArray(ElementName=\"{name}\"  )]\n\t\t[XmlArrayItemAttribute(ElementName=\"{item}\" , IsNullable=false )]\n\t\tpublic GXBaseCollection<{c}> {p}\n\t\t{{\n\t\t\tget {{\n\t\t\t\tif ( {f} == null )\n\t\t\t\t{{\n\t\t\t\t\t{f} = new GXBaseCollection<{c}>( context, \"{dotted}.{item}\", \"\");\n\t\t\t\t}}\n\t\t\t\treturn {f};\n\t\t\t}}\n\t\t\tset {{\n\t\t\t\t{f}_N = false;\n\t\t\t\t{f} = value;\n\t\t\t\tSetDirty(\"{nn}\");\n\t\t\t}}\n\t\t}}\n\n{null_members}\t\tpublic bool ShouldSerialize{p}_GxSimpleCollection_Json()\n\t\t{{\n\t\t\treturn {f} != null && {f}.Count > 0;\n\n\t\t}}\n"
            )
        }
        Kind::Date | Kind::DateTime => {
            let string_class = if m.kind == Kind::Date {
                "GxDateString"
            } else {
                "GxDatetimeString"
            };
            format!(
                "\t\t[SoapElement(ElementName=\"{name}\")]\n\t\t[XmlElement(ElementName=\"{name}\" , IsNullable=true)]\n\t\tpublic string {p}_Nullable\n\t\t{{\n\t\t\tget {{\n\t\t\t\tif ( {f} == DateTime.MinValue)\n\t\t\t\t\treturn null;\n\t\t\t\treturn new {string_class}({f}).value ;\n\t\t\t}}\n\t\t\tset {{\n\t\t\t\t{f} = DateTimeUtil.CToD2(value);\n\t\t\t}}\n\t\t}}\n\n\t\t[XmlIgnore]\n\t\tpublic DateTime {p}\n\t\t{{\n\t\t\tget {{\n\t\t\t\treturn {f}; \n\t\t\t}}\n\t\t\tset {{\n\t\t\t\t{f} = value;\n\t\t\t\tSetDirty(\"{nn}\");\n\t\t\t}}\n\t\t}}\n"
            )
        }
        _ => format!(
            "\t\t[SoapElement(ElementName=\"{name}\")]\n\t\t[XmlElement(ElementName=\"{name}\")]\n\t\tpublic {t} {p}\n\t\t{{\n\t\t\tget {{\n\t\t\t\treturn {f}; \n\t\t\t}}\n\t\t\tset {{\n\t\t\t\t{f} = value;\n\t\t\t\tSetDirty(\"{nn}\");\n\t\t\t}}\n\t\t}}\n",
            t = cs_type(data_type(m).expect("a field"))
        ),
    }
}

fn rest_item(cls: &str, m: &Member<'_>, order: usize) -> String {
    let (name, p) = (m.name, &m.property);
    match m.kind {
        Kind::Level => {
            let c = format!("{}_RESTInterface", child_class(cls, m.node));
            format!(
                "\t\t[DataMember(Name=\"{name}\", Order={order}, EmitDefaultValue=false)]\n\t\tpublic {c} {p}\n\t\t{{\n\t\t\tget {{\n\t\t\t\tif (sdt.ShouldSerialize{p}_Json())\n\t\t\t\t\treturn new {c}(sdt.{p});\n\t\t\t\telse\n\t\t\t\t\treturn null;\n\n\t\t\t}}\n\n\t\t\tset {{\n\t\t\t\tsdt.{p} = value.sdt;\n\t\t\t}}\n\n\t\t}}\n\n"
            )
        }
        Kind::Collection => {
            let c = format!(
                "GxGenericCollection<{}_RESTInterface>",
                child_class(cls, m.node)
            );
            format!(
                "\t\t[DataMember(Name=\"{name}\", Order={order}, EmitDefaultValue=false)]\n\t\tpublic {c} {p}\n\t\t{{\n\t\t\tget {{\n\t\t\t\tif (sdt.ShouldSerialize{p}_GxSimpleCollection_Json())\n\t\t\t\t\treturn new {c}(sdt.{p});\n\t\t\t\telse\n\t\t\t\t\treturn null;\n\n\t\t\t}}\n\t\t\tset {{\n\t\t\t\tvalue.LoadCollection(sdt.{p});\n\t\t\t}}\n\t\t}}\n\n"
            )
        }
        _ => {
            let data_type = data_type(m).expect("a field");
            let (typ, get, set) = match (m.kind, data_type) {
                (Kind::Num, DataType::Numeric { precision, .. }) if *precision > 4 => (
                    " string".to_string(),
                    format!(
                        "StringUtil.LTrim( StringUtil.Str( (decimal) sdt.{p}, {precision}, 0))"
                    ),
                    format!("\t\t\t\tsdt.{p} = (int) NumberUtil.Val( value, \".\");"),
                ),
                (Kind::Num | Kind::Bool, _) => (
                    cs_type(data_type).to_string(),
                    format!("sdt.{p}"),
                    format!("\t\t\t\tsdt.{p} = value;"),
                ),
                (Kind::Char, DataType::Character { .. }) => (
                    " string".to_string(),
                    format!("StringUtil.RTrim( sdt.{p})"),
                    format!("\t\t\t\t sdt.{p} = value;"),
                ),
                (Kind::Char, _) => (
                    " string".to_string(),
                    format!("sdt.{p}"),
                    format!("\t\t\t\t sdt.{p} = value;"),
                ),
                (Kind::Date, _) => (
                    " string".to_string(),
                    format!("DateTimeUtil.DToC2( sdt.{p})"),
                    format!("\t\t\t\tsdt.{p} = DateTimeUtil.CToD2(value);"),
                ),
                _ => (
                    " string".to_string(),
                    format!("DateTimeUtil.TToC2( sdt.{p},context)"),
                    format!("\t\t\t\tsdt.{p} = DateTimeUtil.CToT2(value,context);"),
                ),
            };
            format!(
                "\t\t[DataMember(Name=\"{name}\", Order={order})]\n\t\tpublic {typ} {p}\n\t\t{{\n\t\t\tget {{ \n\t\t\t\treturn {get};\n\n\t\t\t}}\n\t\t\tset {{ \n{set}\n\t\t\t}}\n\t\t}}\n\n"
            )
        }
    }
}
