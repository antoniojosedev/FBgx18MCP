//! The catalog's per-part source cap admits real KB objects. A 1.3 MB Procedure
//! (Plan068Proc in KBTeste) was refused by the old 1 MiB cap.

#![forbid(unsafe_code)]

use gx_engine::catalog::{
    Catalog, KbObject, MAX_SOURCE_PART_BYTES, ObjectId, ObjectKind, SourcePart,
};
use std::collections::BTreeMap;

fn catalog_with_source(bytes: usize) -> Catalog {
    let mut object = KbObject::new(
        ObjectId::new("plan068proc").unwrap(),
        "Plan068Proc",
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "Source".to_string(),
        source: "A".repeat(bytes),
    });
    let mut objects = BTreeMap::new();
    objects.insert(object.id.clone(), object);
    Catalog::from_objects_map(objects)
}

#[test]
fn one_point_three_megabyte_source_part_is_accepted() {
    let json = catalog_with_source(1_338_890)
        .to_json()
        .expect("a real 1.3 MB Procedure source is a valid catalog object");
    assert!(json.contains("plan068proc"));
}

#[test]
fn source_part_one_byte_over_the_cap_is_refused_and_names_the_cap() {
    let error = catalog_with_source(MAX_SOURCE_PART_BYTES + 1)
        .to_json()
        .unwrap_err();
    assert!(
        error.contains(&format!("exceeds the {MAX_SOURCE_PART_BYTES}-byte limit")),
        "{error}"
    );
}
