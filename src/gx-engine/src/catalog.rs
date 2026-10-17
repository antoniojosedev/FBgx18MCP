//! Portable, SDK-independent KB catalog authored for gx-engine.
//!
//! This is not a decoder or writer for GeneXus native KB storage. It models stable
//! object identities, source parts, and explicit references in a bounded JSON file.

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

const FORMAT_VERSION: u32 = 1;
const MAX_CATALOG_BYTES: usize = 8 * 1024 * 1024;
const MAX_OBJECTS: usize = 4096;
const MAX_OBJECT_NAME_BYTES: usize = 128;
const MAX_SOURCE_PARTS: usize = 128;
const MAX_SOURCE_PART_BYTES: usize = 1024 * 1024;
const MAX_REFERENCES_PER_OBJECT: usize = 1024;
const MAX_REFERENCES: usize = 65_536;

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

/// Stable, opaque identity independent of an object's mutable name.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(transparent)]
pub struct ObjectId(String);

impl ObjectId {
    /// Creates a canonical lowercase identity from ASCII letters, digits, `_`, and `-`.
    pub fn new(value: impl Into<String>) -> Result<Self, String> {
        let mut value = value.into();
        validate_token(&value, "object identity", true)?;
        value.make_ascii_lowercase();
        Ok(Self(value))
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ObjectKind {
    Attribute,
    DataProvider,
    DataSelector,
    DataView,
    DesignSystem,
    Domain,
    Module,
    Procedure,
    StructuredDataType,
    Table,
    Transaction,
    WebPanel,
}

#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReferenceKind {
    BasedOn,
    Calls,
    Contains,
    Uses,
}

/// A named authored source part. Part names are canonicalized to ASCII uppercase.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SourcePart {
    pub name: String,
    pub source: String,
}

/// An explicitly typed dependency on another stable object identity.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Reference {
    pub kind: ReferenceKind,
    pub target: ObjectId,
}

/// One object in the portable catalog. The catalog validates and sorts its collections.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct KbObject {
    pub id: ObjectId,
    pub name: String,
    pub kind: ObjectKind,
    #[serde(default)]
    pub source_parts: Vec<SourcePart>,
    #[serde(default)]
    pub references: Vec<Reference>,
}

impl KbObject {
    pub fn new(id: ObjectId, name: impl Into<String>, kind: ObjectKind) -> Self {
        Self {
            id,
            name: name.into(),
            kind,
            source_parts: Vec::new(),
            references: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Edit {
    /// Adds an object or replaces the object with the same stable identity.
    Upsert(KbObject),
    Rename {
        id: ObjectId,
        name: String,
    },
    /// `source: None` removes the named part; `Some` adds or replaces it.
    SetSourcePart {
        id: ObjectId,
        name: String,
        source: Option<String>,
    },
    SetReferences {
        id: ObjectId,
        references: Vec<Reference>,
    },
    /// Fails if any remaining object references this identity.
    Remove {
        id: ObjectId,
    },
}

/// In-memory authored catalog. Mutations validate a complete candidate before publication.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Catalog {
    revision: u64,
    objects: BTreeMap<ObjectId, KbObject>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct CatalogFile {
    format_version: u32,
    revision: u64,
    /// A vector allows the loader to reject duplicate IDs instead of losing them in a JSON map.
    objects: Vec<KbObject>,
}

impl Catalog {
    pub fn empty() -> Self {
        Self {
            revision: 0,
            objects: BTreeMap::new(),
        }
    }

    pub fn from_objects_map(objects: BTreeMap<ObjectId, KbObject>) -> Self {
        Self {
            revision: 1,
            objects,
        }
    }

    pub fn revision(&self) -> u64 {
        self.revision
    }

    pub fn get(&self, id: &ObjectId) -> Option<&KbObject> {
        self.objects.get(id)
    }

    pub fn objects(&self) -> impl Iterator<Item = &KbObject> {
        self.objects.values()
    }

    /// Parses one bounded portable catalog, rejecting unknown fields and duplicate identities.
    pub fn from_json(input: &str) -> Result<Self, String> {
        if input.len() > MAX_CATALOG_BYTES {
            return Err(format!(
                "catalog JSON exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        let file: CatalogFile = serde_json::from_str(input)
            .map_err(|error| format!("invalid catalog JSON: {error}"))?;
        if file.format_version != FORMAT_VERSION {
            return Err(format!(
                "unsupported catalog format_version {}; expected {FORMAT_VERSION}",
                file.format_version
            ));
        }
        if file.objects.len() > MAX_OBJECTS {
            return Err(format!("catalog exceeds the {MAX_OBJECTS}-object limit"));
        }

        let mut objects = BTreeMap::new();
        for object in file.objects {
            let id = canonical_id(object.id.clone())?;
            let mut object = object;
            object.id = id.clone();
            if objects.insert(id.clone(), object).is_some() {
                return Err(format!("duplicate object identity '{}'", id.as_str()));
            }
        }
        let mut catalog = Self {
            revision: file.revision,
            objects,
        };
        catalog.normalize_and_validate()?;
        Ok(catalog)
    }

    /// Serializes validated data in stable identity/name/part/reference order.
    pub fn to_json(&self) -> Result<String, String> {
        let mut catalog = self.clone();
        catalog.normalize_and_validate()?;
        let file = CatalogFile {
            format_version: FORMAT_VERSION,
            revision: catalog.revision,
            objects: catalog.objects.into_values().collect(),
        };
        let output = serde_json::to_string(&file)
            .map_err(|error| format!("could not serialize catalog: {error}"))?;
        if output.len() > MAX_CATALOG_BYTES {
            return Err(format!(
                "serialized catalog exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        Ok(output)
    }

    /// Applies all edits as one optimistic transaction. Failure leaves this catalog unchanged.
    pub fn apply(&mut self, expected_revision: u64, edits: &[Edit]) -> Result<u64, String> {
        if expected_revision != self.revision {
            return Err(format!(
                "stale catalog revision: expected {expected_revision}, current revision is {}",
                self.revision
            ));
        }
        if edits.is_empty() {
            return Ok(self.revision);
        }

        let mut candidate = self.clone();
        for edit in edits {
            match edit {
                Edit::Upsert(object) => {
                    let id = canonical_id(object.id.clone())?;
                    let mut object = object.clone();
                    object.id = id.clone();
                    candidate.objects.insert(id, object);
                }
                Edit::Rename { id, name } => {
                    let object = candidate
                        .objects
                        .get_mut(id)
                        .ok_or_else(|| format!("object '{}' does not exist", id.as_str()))?;
                    object.name = name.clone();
                }
                Edit::SetSourcePart { id, name, source } => {
                    let object = candidate
                        .objects
                        .get_mut(id)
                        .ok_or_else(|| format!("object '{}' does not exist", id.as_str()))?;
                    let name = canonical_part_name(name)?;
                    if let Some(source) = source {
                        if let Some(part) = object.source_parts.iter_mut().find(|p| p.name == name)
                        {
                            part.source.clone_from(source);
                        } else {
                            object.source_parts.push(SourcePart {
                                name,
                                source: source.clone(),
                            });
                        }
                    } else {
                        object.source_parts.retain(|part| part.name != name);
                    }
                }
                Edit::SetReferences { id, references } => {
                    let object = candidate
                        .objects
                        .get_mut(id)
                        .ok_or_else(|| format!("object '{}' does not exist", id.as_str()))?;
                    object.references.clone_from(references);
                }
                Edit::Remove { id } => {
                    if candidate.objects.remove(id).is_none() {
                        return Err(format!("object '{}' does not exist", id.as_str()));
                    }
                }
            }
        }
        candidate.normalize_and_validate()?;
        if candidate.objects == self.objects {
            return Ok(self.revision);
        }
        candidate.revision = self
            .revision
            .checked_add(1)
            .ok_or_else(|| "catalog revision overflow".to_owned())?;
        *self = candidate;
        Ok(self.revision)
    }

    /// Returns a deterministic dependency-first order, or IDs blocked by a dependency cycle.
    /// Cyclic references remain valid catalog data; consumers choose how to handle them.
    pub fn dependency_order(&self) -> Result<Vec<ObjectId>, Vec<ObjectId>> {
        let mut remaining_dependencies = BTreeMap::<ObjectId, usize>::new();
        let mut dependents = BTreeMap::<ObjectId, BTreeSet<ObjectId>>::new();
        for (id, object) in &self.objects {
            let unique_dependencies: BTreeSet<_> = object
                .references
                .iter()
                .map(|reference| reference.target.clone())
                .collect();
            remaining_dependencies.insert(id.clone(), unique_dependencies.len());
            for dependency in unique_dependencies {
                dependents.entry(dependency).or_default().insert(id.clone());
            }
        }

        let mut ready: BTreeSet<_> = remaining_dependencies
            .iter()
            .filter_map(|(id, count)| (*count == 0).then_some(id.clone()))
            .collect();
        let mut ordered = Vec::with_capacity(self.objects.len());
        while let Some(id) = ready.pop_first() {
            ordered.push(id.clone());
            if let Some(users) = dependents.get(&id) {
                for user in users {
                    let count = remaining_dependencies
                        .get_mut(user)
                        .expect("every dependent is a catalog object");
                    *count -= 1;
                    if *count == 0 {
                        ready.insert(user.clone());
                    }
                }
            }
        }

        if ordered.len() == self.objects.len() {
            Ok(ordered)
        } else {
            Err(remaining_dependencies
                .into_iter()
                .filter_map(|(id, count)| (count != 0).then_some(id))
                .collect())
        }
    }

    /// Loads a size-bounded portable catalog or native Knowledge Base from disk.
    pub fn load(path: impl AsRef<Path>) -> Result<Self, String> {
        let path = path.as_ref();
        if path.is_dir() {
            let conn_file = path.join("knowledgebase.connection");
            if conn_file.exists() {
                let client = std::sync::Arc::new(crate::udm::client::UdmClient::connect_kb(path)?);
                let reader = crate::udm::reader::UdmReader::new(client);
                return reader.read_catalog();
            }
        } else if path
            .file_name()
            .is_some_and(|f| f == "knowledgebase.connection")
        {
            let kb_dir = path.parent().unwrap_or(path);
            let client = std::sync::Arc::new(crate::udm::client::UdmClient::connect_kb(kb_dir)?);
            let reader = crate::udm::reader::UdmReader::new(client);
            return reader.read_catalog();
        }

        let file = File::open(path)
            .map_err(|error| format!("could not open catalog '{}': {error}", path.display()))?;
        let metadata = file
            .metadata()
            .map_err(|error| format!("could not inspect catalog '{}': {error}", path.display()))?;
        if metadata.len() > MAX_CATALOG_BYTES as u64 {
            return Err(format!(
                "catalog file exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        let mut bytes = Vec::with_capacity(metadata.len() as usize);
        file.take((MAX_CATALOG_BYTES + 1) as u64)
            .read_to_end(&mut bytes)
            .map_err(|error| format!("could not read catalog '{}': {error}", path.display()))?;
        if bytes.len() > MAX_CATALOG_BYTES {
            return Err(format!(
                "catalog file exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        let text = std::str::from_utf8(&bytes)
            .map_err(|error| format!("catalog file is not UTF-8: {error}"))?;
        Self::from_json(text)
    }

    /// Writes a complete catalog to a sibling temporary file, then atomically renames it.
    /// A failed write or replacement leaves the previous destination untouched.
    pub fn save_atomic(&self, path: impl AsRef<Path>) -> Result<(), String> {
        let path = path.as_ref();
        let bytes = self.to_json()?;
        atomic_write_with(path, bytes.as_bytes(), |file, contents| {
            file.write_all(contents)
        })
        .map_err(|error| {
            format!(
                "could not atomically save catalog '{}': {error}",
                path.display()
            )
        })
    }

    fn normalize_and_validate(&mut self) -> Result<(), String> {
        if self.objects.len() > MAX_OBJECTS {
            return Err(format!("catalog exceeds the {MAX_OBJECTS}-object limit"));
        }

        let mut normalized = BTreeMap::new();
        let mut names = HashSet::with_capacity(self.objects.len());
        let mut total_bytes = 0usize;
        let mut total_references = 0usize;
        for (_, mut object) in std::mem::take(&mut self.objects) {
            object.id = canonical_id(object.id)?;
            normalize_name(&mut object.name, "object name")?;
            add_bounded_bytes(&mut total_bytes, object.id.0.len())?;
            add_bounded_bytes(&mut total_bytes, object.name.len())?;
            if !names.insert(object.name.clone()) {
                return Err(format!(
                    "duplicate object name '{}' (names are case-insensitive)",
                    object.name
                ));
            }
            if object.source_parts.len() > MAX_SOURCE_PARTS {
                return Err(format!(
                    "object '{}' exceeds the {MAX_SOURCE_PARTS}-source-part limit",
                    object.id.as_str()
                ));
            }
            let mut parts = BTreeMap::new();
            for mut part in object.source_parts {
                part.name = canonical_part_name(&part.name)?;
                if part.source.len() > MAX_SOURCE_PART_BYTES {
                    return Err(format!(
                        "source part '{}' on '{}' exceeds the {MAX_SOURCE_PART_BYTES}-byte limit",
                        part.name,
                        object.id.as_str()
                    ));
                }
                if part.source.contains('\0') {
                    return Err(format!(
                        "source part '{}' on '{}' contains a NUL byte",
                        part.name,
                        object.id.as_str()
                    ));
                }
                add_bounded_bytes(&mut total_bytes, part.name.len())?;
                add_bounded_bytes(&mut total_bytes, part.source.len())?;
                if parts.insert(part.name.clone(), part).is_some() {
                    return Err(format!(
                        "duplicate source part name on '{}' (names are case-insensitive)",
                        object.id.as_str()
                    ));
                }
            }
            object.source_parts = parts.into_values().collect();

            if object.references.len() > MAX_REFERENCES_PER_OBJECT {
                return Err(format!(
                    "object '{}' exceeds the {MAX_REFERENCES_PER_OBJECT}-reference limit",
                    object.id.as_str()
                ));
            }
            let mut references = BTreeSet::new();
            for mut reference in object.references {
                reference.target = canonical_id(reference.target)?;
                add_bounded_bytes(&mut total_bytes, reference.target.0.len())?;
                if !references.insert(reference) {
                    return Err(format!(
                        "duplicate reference on object '{}'",
                        object.id.as_str()
                    ));
                }
            }
            total_references = total_references
                .checked_add(references.len())
                .ok_or_else(|| "catalog reference count overflow".to_owned())?;
            if total_references > MAX_REFERENCES {
                return Err(format!(
                    "catalog exceeds the {MAX_REFERENCES}-reference limit"
                ));
            }
            object.references = references.into_iter().collect();
            let id = object.id.clone();
            if normalized.insert(id.clone(), object).is_some() {
                return Err(format!("duplicate object identity '{}'", id.as_str()));
            }
        }

        for object in normalized.values() {
            for reference in &object.references {
                if !normalized.contains_key(&reference.target) {
                    return Err(format!(
                        "object '{}' has a dangling {:?} reference to '{}'",
                        object.id.as_str(),
                        reference.kind,
                        reference.target.as_str()
                    ));
                }
            }
        }
        if total_bytes > MAX_CATALOG_BYTES {
            return Err(format!(
                "catalog content exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        self.objects = normalized;
        Ok(())
    }
}

impl Default for Catalog {
    fn default() -> Self {
        Self::empty()
    }
}

fn canonical_id(mut id: ObjectId) -> Result<ObjectId, String> {
    validate_token(&id.0, "object identity", true)?;
    id.0.make_ascii_lowercase();
    Ok(id)
}

fn normalize_name(name: &mut str, field: &str) -> Result<(), String> {
    validate_token(name, field, false)?;
    name.make_ascii_uppercase();
    Ok(())
}

fn canonical_part_name(name: &str) -> Result<String, String> {
    validate_token(name, "source-part name", false)?;
    Ok(name.to_ascii_uppercase())
}

fn validate_token(value: &str, field: &str, allow_hyphen: bool) -> Result<(), String> {
    let bytes = value.as_bytes();
    if bytes.is_empty() || bytes.len() > MAX_OBJECT_NAME_BYTES {
        return Err(format!(
            "{field} must contain 1 to {MAX_OBJECT_NAME_BYTES} ASCII characters"
        ));
    }
    let first = bytes[0];
    let first_ok = if allow_hyphen {
        first.is_ascii_alphanumeric() || first == b'_' || first == b'-'
    } else {
        first.is_ascii_alphabetic() || first == b'_'
    };
    if !first_ok
        || !bytes.iter().all(|byte| {
            byte.is_ascii_alphanumeric() || *byte == b'_' || (allow_hyphen && *byte == b'-')
        })
    {
        return Err(format!(
            "{field} must use ASCII letters, digits, underscores{}",
            if allow_hyphen { ", or hyphens" } else { "" }
        ));
    }
    Ok(())
}

fn add_bounded_bytes(total: &mut usize, amount: usize) -> Result<(), String> {
    *total = total
        .checked_add(amount)
        .ok_or_else(|| "catalog content size overflow".to_owned())?;
    if *total > MAX_CATALOG_BYTES {
        return Err(format!(
            "catalog content exceeds the {MAX_CATALOG_BYTES}-byte limit"
        ));
    }
    Ok(())
}

fn atomic_write_with(
    path: &Path,
    contents: &[u8],
    write_contents: impl FnOnce(&mut File, &[u8]) -> io::Result<()>,
) -> io::Result<()> {
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let file_name = path.file_name().ok_or_else(|| {
        io::Error::new(io::ErrorKind::InvalidInput, "destination has no file name")
    })?;

    let (temp_path, mut temp_file) = create_temp(parent, file_name)?;
    let result = (|| {
        write_contents(&mut temp_file, contents)?;
        temp_file.sync_all()?;
        drop(temp_file);
        fs::rename(&temp_path, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp_path);
    }
    result
}

fn create_temp(parent: &Path, file_name: &std::ffi::OsStr) -> io::Result<(PathBuf, File)> {
    for _ in 0..32 {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let mut temp_name = file_name.to_os_string();
        temp_name.push(format!(".tmp.{}.{}", std::process::id(), sequence));
        let path = parent.join(temp_name);
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => return Ok((path, file)),
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error),
        }
    }
    Err(io::Error::new(
        io::ErrorKind::AlreadyExists,
        "could not allocate a unique sibling temporary file",
    ))
}

#[cfg(test)]
mod tests {
    use super::atomic_write_with;
    use std::fs;
    use std::io::{self, Write};
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicU64, Ordering};

    static TEST_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    fn temp_dir() -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "gx-engine-catalog-atomic-{}-{}",
            std::process::id(),
            TEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).expect("create test directory");
        path
    }

    #[test]
    fn failed_atomic_write_preserves_the_previous_valid_bytes() {
        let dir = temp_dir();
        let target = dir.join("catalog.json");
        fs::write(&target, b"previous valid catalog").expect("seed destination");

        let error = atomic_write_with(&target, b"new catalog", |file, _| {
            file.write_all(b"partial temporary data")?;
            Err(io::Error::other("injected write failure"))
        })
        .expect_err("injected write must fail");

        assert_eq!(error.kind(), io::ErrorKind::Other);
        assert_eq!(
            fs::read(&target).expect("previous bytes remain"),
            b"previous valid catalog"
        );
        assert_eq!(fs::read_dir(&dir).expect("read test dir").count(), 1);
        fs::remove_dir_all(dir).expect("remove test directory");
    }
}
