//! GxUserType C# class generator for Structured Data Types (SDTs).
//!
//! The class shape follows the GeneXus-generated `type_Sdt*.cs` of the evidence KBs (see
//! `docs/udm_storage_notes.md`, "SDT structure"): one `Sdt<Name>` class with a `gxTpr_<Member>`
//! property per field, a nested `Sdt<Name>_<Level>` class per level, and a
//! `GXBaseCollection<Sdt<Name>_<Level>_<Item>>` for each collection level. The member name keeps the
//! first letter and lower-cases the rest (`ChildValue` â†’ `gxTpr_Childvalue`). JSON emission covers field values
//! and initialized child levels; REST wrappers and XML serialization remain outside this contract.

use crate::{
    catalog::{KbObject, ObjectKind},
    csharp::types::{to_csharp_default_value, to_csharp_type},
    model::DataType,
    udm::stored_sdt::SdtNode,
};
use std::collections::BTreeMap;
use std::fmt::Write;

/// One emittable SDT class: its name (without `Sdt`), namespace and decoded top-level nodes. `sdk_name`
/// is the dotted GeneXus name the SDK writes in a collection's `new GXBaseCollection<..>(.., "<name>", ..)`.
#[derive(Debug, Clone)]
pub struct SdtClass {
    pub name: String,
    pub namespace: String,
    pub nodes: Vec<SdtNode>,
    pub sdk_name: String,
    /// Namespaces imported after the standard ones (see `ProcedureClass::usings`).
    pub usings: Vec<String>,
    /// The XML namespace the SDK writes on the SDT's classes (`ExternalNamespace`, else the KB's name),
    /// when the reader read it. Only the SDK form (`csharp::sdt_sdk`) uses it.
    pub xml_namespace: Option<String>,
    /// The SDT's root is a collection: the class is its item class (see `from_kb_object`).
    pub root_collection: bool,
}

/// The portable JSON the reader holds for a StructuredDataType: `{"fields": [...]}`, plus the item name
/// of a collection root (`root_collection`) when the SDT's root is a collection.
#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct PortableSdt {
    fields: Vec<SdtNode>,
    #[serde(default)]
    root_collection: Option<String>,
    #[serde(default)]
    xml_namespace: Option<String>,
}

impl SdtClass {
    pub fn new(name: impl Into<String>) -> Self {
        let name = name.into();
        Self {
            sdk_name: name.clone(),
            name,
            namespace: "GeneXus.Programs".to_string(),
            nodes: Vec::new(),
            usings: Vec::new(),
            xml_namespace: None,
            root_collection: false,
        }
    }

    pub fn with_namespace(mut self, namespace: impl Into<String>) -> Self {
        self.namespace = namespace.into();
        self
    }

    /// Imports the module namespaces of the build's SDTs (its own namespace is never repeated).
    pub fn with_usings(mut self, usings: Vec<String>) -> Self {
        self.usings = usings;
        self
    }

    /// Adds a scalar field of the given type.
    pub fn add_field(mut self, name: impl Into<String>, data_type: DataType) -> Self {
        self.nodes.push(SdtNode::Field {
            name: name.into(),
            data_type,
        });
        self
    }

    /// The C# class name of this SDT (`Sdt<Name>`).
    pub fn class_name(&self) -> String {
        format!("Sdt{}", self.name)
    }

    /// The classes `emit_class` writes for this SDT's levels, one `SdtClass` each, so the index
    /// resolves the members of a Procedure variable typed by a nested level. Each is named as the
    /// emitter names it (`<holder>_<level>`, or `<holder>_<item>` for a collection item).
    pub fn level_classes(&self) -> Vec<SdtClass> {
        let mut out = Vec::new();
        collect_level_classes(
            &self.name,
            &self.sdk_name,
            &self.namespace,
            &self.nodes,
            &mut out,
        );
        out
    }

    /// Builds the class from a StructuredDataType object. The object's portable structure is read
    /// from its `STRUCTURE` (or legacy `Fields`) part. A structure that is absent or does not parse
    /// is an error: an SDT is never emitted as an empty class.
    pub fn from_kb_object(object: &KbObject) -> Result<Self, String> {
        Self::from_kb_object_at_root(object).map(|class| {
            class.with_namespace(crate::csharp::types::module_namespace(&object.module_path))
        })
    }

    fn from_kb_object_at_root(object: &KbObject) -> Result<Self, String> {
        if object.kind != ObjectKind::StructuredDataType {
            return Err(format!(
                "expected StructuredDataType, found {:?}",
                object.kind
            ));
        }
        let part = object
            .source_parts
            .iter()
            .find(|p| {
                p.name.eq_ignore_ascii_case("STRUCTURE") || p.name.eq_ignore_ascii_case("Fields")
            })
            .ok_or_else(|| format!("SDT '{}' has no structure part", object.name))?;
        let portable: PortableSdt = serde_json::from_str(&part.source).map_err(|e| {
            format!(
                "SDT '{}' structure is not the portable form this engine reads: {e}",
                object.name
            )
        })?;
        if portable.fields.is_empty() {
            return Err(format!("SDT '{}' structure declares no items", object.name));
        }
        // A collection root has no class of its own in the SDK: the SDT is its item class, named
        // `<SDT>_<item>`, and the items sit directly under it (`SdtProgramNames_ProgramName`).
        let mut sdt = match portable.root_collection {
            Some(item) => {
                let mut class = Self::new(format!("{}_{item}", object.name));
                class.sdk_name = format!("{}.{item}", object.name);
                class.root_collection = true;
                class
            }
            None => Self::new(&object.name),
        };
        sdt.nodes = portable.fields;
        sdt.xml_namespace = portable.xml_namespace;
        Ok(sdt)
    }

    /// Emits the SDT: the top-level class, then one class per level, in document order.
    pub fn emit(&self) -> String {
        let mut out = String::new();
        let _ = writeln!(out, "using System;");
        let _ = writeln!(out, "using System.Collections;");
        let _ = writeln!(out, "using GeneXus.Utils;");
        let _ = writeln!(out, "using GeneXus.Application;");
        let _ = writeln!(out, "using System.Xml.Serialization;");
        let _ = writeln!(out, "using System.Runtime.Serialization;");
        for namespace in self.usings.iter().filter(|n| **n != self.namespace) {
            let _ = writeln!(out, "using {namespace};");
        }
        let _ = writeln!(out);
        let _ = writeln!(out, "namespace {} {{", self.namespace);
        emit_class(&mut out, &self.class_name(), &self.sdk_name, &self.nodes);
        let _ = writeln!(out, "}}");
        out
    }
}

/// The C# property name of a stored member: `gxTpr_` plus the name with its first letter upper-cased
/// and the rest lower-cased, as the GeneXus generator writes it.
pub(crate) fn member_name(name: &str) -> String {
    let mut chars = name.chars();
    match chars.next() {
        None => "gxTpr_".to_string(),
        // The first letter is upper-cased too: `id` is `gxTpr_Id` (general/ui/sidebaritemsdp.cs).
        Some(first) => format!(
            "gxTpr_{}{}",
            first.to_ascii_uppercase(),
            chars.as_str().to_lowercase()
        ),
    }
}

/// The name of a collection level's item as the SDK writes it: the stored `idCollectionItemName`, or
/// `<level name>Item` when there is none.
fn item_suffix(item_name: Option<&str>, level: &str) -> String {
    match item_name {
        Some(item) => item.to_string(),
        None => format!("{level}Item"),
    }
}

/// The backing field of a member: `gxTv_<class>_<name>`, as the SDK names it (`gxTv_SdtRSN03944700_Rows`).
fn backing_field(class_name: &str, member: &str) -> String {
    format!("gxTv_{class_name}_{}", member.trim_start_matches("gxTpr_"))
}

/// The item class of a collection level: the class that holds the collection member, joined to the
/// item name by `_`. The evidence: a top-level collection is `Sdt<root>_<item>`
/// (`SdtRSN03944700_RowsItem`, `SdtRCE06829400_Entry`); a collection inside a nested level is
/// `Sdt<root>_<parent>_<item>` (`SdtNativeDeepSdt85299100_Child_NestedCollectionItem`). The item name
/// is the stored `idCollectionItemName` when there is one, otherwise `<level name>Item` (the default
/// seen on `RSN03944700.Rows`, giving `RowsItem`).
pub(crate) fn collection_item_class(holder: &str, item_name: Option<&str>, level: &str) -> String {
    match item_name {
        Some(item) => format!("{holder}_{item}"),
        None => format!("{holder}_{level}Item"),
    }
}

/// The level classes under `holder` (a schema name without `Sdt`), depth first. `dotted` is the GeneXus
/// name of `holder`, which the classes below it extend.
fn collect_level_classes(
    holder: &str,
    dotted: &str,
    namespace: &str,
    nodes: &[SdtNode],
    out: &mut Vec<SdtClass>,
) {
    for node in nodes {
        let SdtNode::Level {
            name,
            collection,
            item_name,
            children,
        } = node
        else {
            continue;
        };
        let (schema, level_dotted) = if *collection {
            let item = item_name.clone().unwrap_or_else(|| format!("{name}Item"));
            (format!("{holder}_{item}"), format!("{dotted}.{item}"))
        } else {
            (format!("{holder}_{name}"), format!("{dotted}.{name}"))
        };
        out.push(SdtClass {
            name: schema.clone(),
            namespace: namespace.to_string(),
            nodes: children.clone(),
            sdk_name: level_dotted.clone(),
            usings: Vec::new(),
            xml_namespace: None,
            root_collection: false,
        });
        collect_level_classes(&schema, &level_dotted, namespace, children, out);
    }
}

/// The `_gxi` companion of an image member. The SDK declares it next to the image's own string:
/// `public string gxTpr_<Member>_gxi` over `gxTv_<class>_<Member>_gxi`, initialised to `""` (SDTs.stg,
/// `add_gxi_property`, `declare_item_variable` and `includeGXI`). It stores the image's GeneXus URL.
fn emit_image_companion(out: &mut String, class_name: &str, name: &str, member: &str) {
    let companion = format!("{member}_gxi");
    let backing = format!("{}_gxi", backing_field(class_name, member));
    let _ = writeln!(out, "      public string {companion}");
    let _ = writeln!(out, "      {{");
    let _ = writeln!(out, "         get {{ return {backing}; }}");
    let _ = writeln!(
        out,
        "         set {{ {backing} = value; SetDirty(\"{name}_gxi\"); }}"
    );
    let _ = writeln!(out, "      }}");
    let _ = writeln!(out, "      protected string {backing} = \"\";");
}

/// Registers fields with the runtime JSON writer; child getters must not instantiate absent levels.
fn emit_json(out: &mut String, class_name: &str, nodes: &[SdtNode]) {
    let _ = writeln!(
        out,
        "      public override void ToJSON() {{ ToJSON(true); }}"
    );
    let _ = writeln!(out, "      public override void ToJSON(bool includeState)");
    let _ = writeln!(out, "      {{");
    for node in nodes {
        let (name, data_type) = match node {
            SdtNode::Field { name, data_type } => (name, Some(data_type)),
            SdtNode::Level { name, .. } => (name, None),
        };
        let member = member_name(name);
        let backing = backing_field(class_name, &member);
        let value = match data_type {
            Some(DataType::Date {} | DataType::DateTime {}) => {
                let mut format = "{0:D4}-{1:D2}-{2:D2}".to_string();
                let mut parts = ["Year", "Month", "Day"]
                    .map(|part| format!("DateTimeUtil.{part}({member})"))
                    .to_vec();
                if matches!(data_type, Some(DataType::DateTime {})) {
                    format.push_str("T{3:D2}:{4:D2}:{5:D2}");
                    parts.extend(
                        ["Hour", "Minute", "Second"]
                            .map(|part| format!("DateTimeUtil.{part}({member})")),
                    );
                }
                format!(
                    "string.Format(System.Globalization.CultureInfo.InvariantCulture, \"{format}\", {})",
                    parts.join(", ")
                )
            }
            Some(DataType::Guid {}) => format!("{member}.ToString()"),
            _ => member,
        };
        if data_type.is_none() || matches!(data_type, Some(DataType::Record { .. })) {
            let _ = writeln!(out, "         if ({backing} != null)");
            let _ = writeln!(
                out,
                "            AddObjectProperty(\"{name}\", {backing}, false);"
            );
        } else {
            let _ = writeln!(
                out,
                "         AddObjectProperty(\"{name}\", {value}, false);"
            );
        }
    }
    let _ = writeln!(out, "      }}");
}

/// Emits one class, then the classes of its levels (a nested level is a class of its own; a
/// collection level's item is a class of its own, holding the level's children). A collection's item
/// class is named from the class that holds the collection, so the recursion needs no root name.
fn emit_class(out: &mut String, class_name: &str, sdk_name: &str, nodes: &[SdtNode]) {
    let _ = writeln!(out, "   [Serializable]");
    let _ = writeln!(out, "   public class {class_name} : GxUserType");
    let _ = writeln!(out, "   {{");
    let _ = writeln!(out, "      public {class_name}( ) {{ }}");
    let _ = writeln!(out);
    let _ = writeln!(out, "      public {class_name}( IGxContext context )");
    let _ = writeln!(out, "      {{");
    let _ = writeln!(out, "         this.context = context;");
    let _ = writeln!(out, "      }}");
    let _ = writeln!(out);
    for node in nodes {
        match node {
            SdtNode::Field { name, data_type } => {
                let member = member_name(name);
                let backing = backing_field(class_name, &member);
                let cs_type = to_csharp_type(data_type);
                let _ = writeln!(out, "      public {cs_type} {member}");
                let _ = writeln!(out, "      {{");
                if let DataType::Record { .. } = data_type {
                    // An SDT-typed member is created on first access, as a nested level is (the SDK's
                    // lazy getter, type_SdtRSN03944700.cs gxTpr_Child).
                    let _ = writeln!(out, "         get {{");
                    let _ = writeln!(out, "            if ( {backing} == null )");
                    let _ = writeln!(out, "            {{");
                    let _ = writeln!(out, "               {backing} = new {cs_type}(context);");
                    let _ = writeln!(out, "            }}");
                    let _ = writeln!(out, "            return {backing};");
                    let _ = writeln!(out, "         }}");
                    let _ = writeln!(
                        out,
                        "         set {{ {backing} = value; SetDirty(\"{name}\"); }}"
                    );
                    let _ = writeln!(out, "      }}");
                    let _ = writeln!(out, "      protected {cs_type} {backing} = null;");
                } else {
                    // A scalar keeps its SDK default ("", 0, DateTime.MinValue) in its backing field.
                    let _ = writeln!(out, "         get {{ return {backing}; }}");
                    let _ = writeln!(
                        out,
                        "         set {{ {backing} = value; SetDirty(\"{name}\"); }}"
                    );
                    let _ = writeln!(out, "      }}");
                    let _ = writeln!(
                        out,
                        "      protected {cs_type} {backing} = {};",
                        to_csharp_default_value(data_type)
                    );
                    if let DataType::Image {} = data_type {
                        emit_image_companion(out, class_name, name, &member);
                    }
                }
                let _ = writeln!(out);
            }
            SdtNode::Level {
                name,
                collection,
                item_name,
                ..
            } => {
                // The SDK creates a level's collection or child on first access, in the getter, so
                // `AV.gxTpr_Rows` is never null (artdx765000a and type_SdtRSN03944700.cs).
                let member = member_name(name);
                let backing = backing_field(class_name, &member);
                if *collection {
                    let item = collection_item_class(class_name, item_name.as_deref(), name);
                    let item_sdk =
                        format!("{sdk_name}.{}", item_suffix(item_name.as_deref(), name));
                    let _ = writeln!(
                        out,
                        "      protected GXBaseCollection<{item}> {backing} = null;"
                    );
                    let _ = writeln!(out, "      public GXBaseCollection<{item}> {member}");
                    let _ = writeln!(out, "      {{");
                    let _ = writeln!(out, "         get {{");
                    let _ = writeln!(out, "            if ( {backing} == null )");
                    let _ = writeln!(out, "            {{");
                    let _ = writeln!(
                        out,
                        "               {backing} = new GXBaseCollection<{item}>( context, \"{item_sdk}\", \"\");"
                    );
                    let _ = writeln!(out, "            }}");
                    let _ = writeln!(out, "            return {backing};");
                    let _ = writeln!(out, "         }}");
                    let _ = writeln!(
                        out,
                        "         set {{ {backing} = value; SetDirty(\"{name}\"); }}"
                    );
                    let _ = writeln!(out, "      }}");
                } else {
                    let nested = format!("{class_name}_{name}");
                    let _ = writeln!(out, "      protected {nested} {backing} = null;");
                    let _ = writeln!(out, "      public {nested} {member}");
                    let _ = writeln!(out, "      {{");
                    let _ = writeln!(out, "         get {{");
                    let _ = writeln!(out, "            if ( {backing} == null )");
                    let _ = writeln!(out, "            {{");
                    let _ = writeln!(out, "               {backing} = new {nested}(context);");
                    let _ = writeln!(out, "            }}");
                    let _ = writeln!(out, "            return {backing};");
                    let _ = writeln!(out, "         }}");
                    let _ = writeln!(
                        out,
                        "         set {{ {backing} = value; SetDirty(\"{name}\"); }}"
                    );
                    let _ = writeln!(out, "      }}");
                }
                let _ = writeln!(out);
            }
        }
    }
    emit_json(out, class_name, nodes);
    let _ = writeln!(out, "   }}");
    // The level classes follow the class that holds them, so a nested level is never emitted
    // before the class it belongs to.
    for node in nodes {
        if let SdtNode::Level {
            name,
            collection,
            item_name,
            children,
        } = node
        {
            let nested = format!("{class_name}_{name}");
            if *collection {
                let item = collection_item_class(class_name, item_name.as_deref(), name);
                let item_sdk = format!("{sdk_name}.{}", item_suffix(item_name.as_deref(), name));
                emit_class(out, &item, &item_sdk, children);
            } else {
                let nested_sdk = format!("{sdk_name}.{name}");
                emit_class(out, &nested, &nested_sdk, children);
            }
        }
    }
}

/// The SDTs this build emits, keyed by their C# class name (compared without case). A Procedure
/// resolves each member access against this index. An SDT that is not in it (refused by the reader,
/// dropped by the dependency walk, or not generated) is never resolved, so a member of it fails
/// closed instead of being emitted against a class that does not exist.
#[derive(Debug, Clone, Default)]
pub struct SdtIndex {
    classes: BTreeMap<String, SdtClass>,
}

impl SdtIndex {
    /// Builds the index. Two SDTs with one class name are an error: a member would resolve to either.
    pub fn from_classes(classes: impl IntoIterator<Item = SdtClass>) -> Result<Self, String> {
        let mut index = Self::default();
        for class in classes {
            let key = class.class_name().to_ascii_lowercase();
            if index.classes.contains_key(&key) {
                return Err(format!(
                    "two SDTs share the class name '{}'; refusing to resolve members of either",
                    class.class_name()
                ));
            }
            index.classes.insert(key, class);
        }
        Ok(index)
    }

    /// The class this build emits under `class_name`, if it emits one.
    /// Every class of the index.
    pub fn classes(&self) -> impl Iterator<Item = &SdtClass> {
        self.classes.values()
    }

    pub fn class(&self, class_name: &str) -> Option<&SdtClass> {
        self.classes.get(&class_name.to_ascii_lowercase())
    }
}

/// What an SDT-valued expression is in the emitted C#.
#[derive(Debug, Clone)]
pub(crate) enum SdtMemberType<'a> {
    /// An SDT or a non-collection level, emitted as `class`; `children` are its members.
    Class {
        class: String,
        children: &'a [SdtNode],
    },
    /// A collection level: `Item(n)` yields an object of class `item`, whose members are `children`.
    Collection {
        item: String,
        children: &'a [SdtNode],
    },
    /// A scalar member of the given type, which has no members of its own.
    Scalar(DataType),
    /// A collection's `Count` property: a number with no declared type in the stored model.
    Count,
}

/// The name a node carries in the stored structure.
pub(crate) fn node_name(node: &SdtNode) -> &str {
    match node {
        SdtNode::Field { name, .. } | SdtNode::Level { name, .. } => name,
    }
}

/// The type of a variable declared as the SDT with C# type `declared`. Fails when this build does not
/// emit that SDT, so its members cannot be resolved.
pub(crate) fn sdt_type<'a>(
    index: &'a SdtIndex,
    declared: &str,
) -> Result<SdtMemberType<'a>, String> {
    let class = index.class(declared).ok_or_else(|| {
        format!(
            "SDT '{declared}' is not emitted by this build (refused, dropped or absent), so its members cannot be resolved"
        )
    })?;
    Ok(SdtMemberType::Class {
        class: class.class_name(),
        children: &class.nodes,
    })
}

/// The C# member name and type of `field` on `parent`, as the stored structure declares it. The match
/// is case-insensitive, as GeneXus names are; the C# name is the SDK's `gxTpr_` form.
pub(crate) fn sdt_member<'a>(
    parent: &SdtMemberType<'a>,
    field: &str,
) -> Result<(String, SdtMemberType<'a>), String> {
    match parent {
        SdtMemberType::Class { class, children } => {
            let children: &'a [SdtNode] = children;
            let found: Vec<&'a SdtNode> = children
                .iter()
                .filter(|node| node_name(node).eq_ignore_ascii_case(field))
                .collect();
            match found.as_slice() {
                [] => Err(format!("no such member in {class}")),
                [node] => Ok(match node {
                    SdtNode::Field { name, data_type } => {
                        (member_name(name), SdtMemberType::Scalar(data_type.clone()))
                    }
                    SdtNode::Level {
                        name,
                        collection: false,
                        children,
                        ..
                    } => (
                        member_name(name),
                        SdtMemberType::Class {
                            class: format!("{class}_{name}"),
                            children,
                        },
                    ),
                    SdtNode::Level {
                        name,
                        collection: true,
                        item_name,
                        children,
                    } => (
                        member_name(name),
                        SdtMemberType::Collection {
                            item: collection_item_class(class, item_name.as_deref(), name),
                            children,
                        },
                    ),
                }),
                _ => Err(format!("more than one member of {class} has this name")),
            }
        }
        // A collection's own property: the SDK writes `AV8First.gxTpr_Rows.Count` (GXBaseCollection).
        SdtMemberType::Collection { .. } if field.eq_ignore_ascii_case("COUNT") => {
            Ok(("Count".to_string(), SdtMemberType::Count))
        }
        SdtMemberType::Collection { .. } => {
            Err("the receiver is a collection; Item(n) must come first".to_string())
        }
        SdtMemberType::Scalar(_) => {
            Err("the receiver is a scalar, which has no members".to_string())
        }
        SdtMemberType::Count => Err("the receiver is a count, which has no members".to_string()),
    }
}

/// The item object `Item(n)` yields on a collection member.
pub(crate) fn sdt_item<'a>(parent: &SdtMemberType<'a>) -> Result<SdtMemberType<'a>, String> {
    match parent {
        SdtMemberType::Collection { item, children } => {
            let children: &'a [SdtNode] = children;
            Ok(SdtMemberType::Class {
                class: item.clone(),
                children,
            })
        }
        _ => Err("Item(n) is applied to something that is not a collection".to_string()),
    }
}
