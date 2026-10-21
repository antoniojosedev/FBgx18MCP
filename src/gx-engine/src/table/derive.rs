//! Derivation of physical `Table` schemas, with their foreign keys, from Transactions.

use super::*;

/// Derives relational physical `Table` schemas from a `TransactionStructure`.
///
/// Follows GeneXus relational table derivation:
/// - Root level maps to the root Table with its primary key.
/// - Child levels inherit parent primary keys as both primary key parts and Foreign Keys.
/// - Formula attributes are omitted from physical storage.
/// - Attribute data types are resolved using the provided `attribute_resolver` when not inline.
pub fn derive_tables_from_transaction(
    structure: &TransactionStructure,
    attribute_resolver: impl Fn(&str) -> Option<DataType>,
) -> Result<Vec<Table>, String> {
    structure.validate()?;
    let mut tables = Vec::new();
    let inherited_keys = Vec::new();
    derive_level_table(
        &structure.root,
        None,
        &inherited_keys,
        &attribute_resolver,
        &mut tables,
        0,
    )?;
    Ok(tables)
}

fn derive_level_table(
    level: &TransactionLevel,
    parent_table_name: Option<&str>,
    inherited_keys: &[TableColumn],
    resolver: &impl Fn(&str) -> Option<DataType>,
    tables: &mut Vec<Table>,
    depth: usize,
) -> Result<(), String> {
    if depth > 16 {
        return Err("Transaction level nesting depth exceeds limit of 16".into());
    }

    let table_name = match parent_table_name {
        None => level.name.clone(),
        Some(parent) => {
            if level
                .name
                .to_ascii_uppercase()
                .starts_with(&parent.to_ascii_uppercase())
            {
                level.name.clone()
            } else {
                format!("{parent}{}", level.name)
            }
        }
    };

    let mut columns: Vec<TableColumn> = Vec::new();
    let mut primary_key: Vec<String> = Vec::new();
    let mut seen_col_names = BTreeSet::new();

    // 1. Add inherited parent primary keys (for child tables)
    for parent_key in inherited_keys.iter() {
        let mut key_col = parent_key.clone();
        key_col.is_key = true;
        key_col.is_nullable = false;
        primary_key.push(key_col.name.clone());
        seen_col_names.insert(key_col.name.to_ascii_uppercase());
        columns.push(key_col);
    }

    // 2. Add attributes from this level
    for attr in &level.attributes {
        // Skip formula attributes (in GeneXus formulas are virtual/non-stored)
        if attr.formula.is_some() {
            continue;
        }

        let upper_name = attr.name.to_ascii_uppercase();
        if seen_col_names.contains(&upper_name) {
            // Already present from inherited keys
            continue;
        }

        let data_type = if let Some(ref dt) = attr.data_type {
            dt.clone()
        } else if let Some(dt) = resolver(&attr.name) {
            dt
        } else {
            return Err(format!(
                "cannot resolve data type for attribute '{}' in level '{}'",
                attr.name, level.name
            ));
        };

        let col = TableColumn {
            name: attr.name.clone(),
            data_type,
            is_key: attr.is_key,
            is_nullable: if attr.is_key { false } else { attr.is_nullable },
            description: None,
        };

        if attr.is_key {
            primary_key.push(col.name.clone());
        }

        seen_col_names.insert(upper_name);
        columns.push(col);
    }

    // 3. Foreign key to parent table (for child tables)
    let mut foreign_keys = Vec::new();
    if let Some(parent) = parent_table_name.filter(|_| !inherited_keys.is_empty()) {
        let parent_pk_names: Vec<String> = inherited_keys.iter().map(|k| k.name.clone()).collect();
        let fk_name = format!("FK_{table_name}_{parent}");
        foreign_keys.push(TableForeignKey::new(
            fk_name,
            parent_pk_names.clone(),
            parent,
            parent_pk_names,
        ));
    }

    // 4. Primary index
    let mut indexes = Vec::new();
    let pk_index_name = format!("U{table_name}");
    let mut pk_index = TableIndex::new(pk_index_name, primary_key.clone(), true);
    pk_index.is_primary = true;
    indexes.push(pk_index);

    let table = Table {
        name: table_name.clone(),
        description: level.description.clone(),
        columns,
        primary_key,
        foreign_keys,
        indexes,
    };
    table.validate()?;
    tables.push(table);

    // 5. Descend to child levels with accumulated primary keys
    let mut current_level_keys = Vec::new();
    for col in &tables
        .last()
        .expect("the table of this level was pushed just above")
        .columns
    {
        if col.is_key {
            current_level_keys.push(col.clone());
        }
    }

    for child in &level.levels {
        derive_level_table(
            child,
            Some(&table_name),
            &current_level_keys,
            resolver,
            tables,
            depth + 1,
        )?;
    }

    Ok(())
}

/// Derives all physical tables across all Transactions in a `Catalog`.
///
/// Automatically resolves attribute types through Catalog Attributes and Domains,
/// infers cross-transaction foreign keys where non-key attributes match another table's primary key,
/// and returns tables in dependency order.
pub fn derive_tables_from_catalog(catalog: &Catalog) -> Result<Vec<Table>, String> {
    let (tables, failures) = derive_tables_partial(catalog);
    match failures.first() {
        None => Ok(tables),
        Some(_) => Err(failures.join("; ")),
    }
}

/// Derives the physical tables of every Transaction that can be derived, and names each one that cannot.
///
/// A Transaction is derived or refused on its own: one refused Transaction does not stop the others. Each failure
/// is one message naming its Transaction, in catalog order. A Transaction whose key references cannot be resolved
/// is refused the same way (see `resolve_key_references`). The tables returned are those of the Transactions that
/// derived, with their key references and the cross-Transaction foreign keys inferred among them.
pub fn derive_tables_partial(catalog: &Catalog) -> (Vec<Table>, Vec<String>) {
    // Map attribute name -> DataType using catalog Attributes and Domains
    let resolve_attr = |name: &str| -> Option<DataType> {
        let mut visited = BTreeSet::new();
        crate::kb::resolve_domain_or_attribute_data_type(catalog, name, &mut visited, 0).ok()
    };

    // One outcome per Transaction, in catalog order: its tables, or the reason they cannot be derived.
    let mut outcomes: Vec<Result<DerivedTransaction, String>> = Vec::new();
    for obj in catalog.objects() {
        if obj.kind != ObjectKind::Transaction {
            continue;
        }
        outcomes.push(derive_transaction_tables(obj, &resolve_attr));
    }
    resolve_key_references(&mut outcomes);

    let mut all_tables = Vec::new();
    let mut table_names = BTreeSet::new();
    let mut failures = Vec::new();
    for outcome in outcomes {
        match outcome {
            Ok(derived) => {
                for tbl in derived.tables {
                    let key = tbl.name.to_ascii_uppercase();
                    if table_names.insert(key) {
                        all_tables.push(tbl);
                    }
                }
            }
            Err(reason) => failures.push(reason),
        }
    }

    // Infer cross-transaction foreign keys:
    // If table A has a non-key column whose name matches the single primary key of table B,
    // and no foreign key exists yet, add a foreign key and index!
    let mut primary_keys: BTreeMap<String, (String, String)> = BTreeMap::new();
    for tbl in &all_tables {
        if tbl.primary_key.len() == 1 {
            let pk_name = tbl.primary_key[0].clone();
            primary_keys.insert(pk_name.to_ascii_uppercase(), (tbl.name.clone(), pk_name));
        }
    }

    for tbl in &mut all_tables {
        let current_table_name = tbl.name.clone();
        for col in &tbl.columns {
            if col.is_key {
                continue;
            }
            let col_upper = col.name.to_ascii_uppercase();
            if let Some((target_tbl, target_pk)) = primary_keys.get(&col_upper) {
                if target_tbl.eq_ignore_ascii_case(&current_table_name) {
                    continue;
                }
                // Check if FK already exists
                let already_exists = tbl
                    .foreign_keys
                    .iter()
                    .any(|fk| fk.referenced_table.eq_ignore_ascii_case(target_tbl));
                if !already_exists {
                    let fk_name = format!("FK_{current_table_name}_{target_tbl}");
                    tbl.foreign_keys.push(TableForeignKey::new(
                        fk_name,
                        vec![col.name.clone()],
                        target_tbl.clone(),
                        vec![target_pk.clone()],
                    ));
                    let idx_name = format!("I{current_table_name}_{target_tbl}");
                    tbl.indexes
                        .push(TableIndex::new(idx_name, vec![col.name.clone()], false));
                }
            }
        }
    }

    (all_tables, failures)
}

/// A Transaction's resolved key references: the index of its root table, and each foreign key with the name of
/// the Transaction it targets.
type ResolvedKeys = (usize, Vec<(TableForeignKey, String)>);

/// One Transaction's derived tables, with the key references its structure declares.
struct DerivedTransaction {
    name: String,
    tables: Vec<Table>,
    key_references: Vec<KeyReference>,
}

/// Resolves the key references of the Transactions that derived, and attaches each resolved one to the root table
/// of its Transaction as a foreign key.
///
/// A reference is refused when it names no derived table's whole primary key, names more than one (ambiguous),
/// names a key of another type, or names the same table twice. A Transaction whose references lead back to itself
/// through the others is refused as a cycle. A refused Transaction becomes a failure in place, and the others are
/// resolved again, because a refused target is no longer derived. This repeats until no Transaction is refused.
/// Nothing is defaulted, and no reference points at a table that is not derived.
fn resolve_key_references(outcomes: &mut [Result<DerivedTransaction, String>]) {
    loop {
        // Each Transaction that declares references: its index, its name, and its root table and links, or why not.
        let mut planned: Vec<(usize, String, Result<ResolvedKeys, String>)> = Vec::new();
        {
            let candidates: Vec<(&str, &Table)> = outcomes
                .iter()
                .filter_map(|outcome| outcome.as_ref().ok())
                .flat_map(|derived| {
                    derived
                        .tables
                        .iter()
                        .map(move |table| (derived.name.as_str(), table))
                })
                .collect();
            for (index, outcome) in outcomes.iter().enumerate() {
                let Ok(owner) = outcome else { continue };
                if owner.key_references.is_empty() {
                    continue;
                }
                planned.push((
                    index,
                    owner.name.clone(),
                    foreign_keys_of(owner, &candidates),
                ));
            }
        }

        // The Transaction-level graph of the references that resolved: owner -> the Transaction each one names.
        let mut edges: BTreeMap<String, Vec<String>> = BTreeMap::new();
        for (_, name, links) in &planned {
            if let Ok((_, links)) = links {
                edges.insert(
                    name.to_ascii_uppercase(),
                    links
                        .iter()
                        .map(|(_, target)| target.to_ascii_uppercase())
                        .collect(),
                );
            }
        }

        let mut refused: Vec<(usize, String)> = Vec::new();
        for (index, name, links) in &planned {
            match links {
                Err(reason) => refused.push((*index, format!("Transaction '{name}': {reason}"))),
                Ok(_) if reaches_itself(&name.to_ascii_uppercase(), &edges) => refused.push((
                    *index,
                    format!(
                        "Transaction '{name}': its key references form a cycle through '{name}'"
                    ),
                )),
                Ok(_) => {}
            }
        }

        if refused.is_empty() {
            // Every planned Transaction resolved, so every plan is `Ok`: the foreign keys go on the root table
            // whose index was found while planning, and nothing is attached to any other table.
            for (index, _, links) in planned {
                let Ok((root_index, links)) = links else {
                    continue;
                };
                if let Ok(owner) = &mut outcomes[index] {
                    owner.tables[root_index]
                        .foreign_keys
                        .extend(links.into_iter().map(|(fk, _)| fk));
                }
            }
            return;
        }
        for (index, reason) in refused {
            outcomes[index] = Err(reason);
        }
    }
}

/// The foreign keys that one Transaction's key references make, each with the name of the Transaction it targets.
/// The result also gives the index of the Transaction's root table, where the keys go.
///
/// A reference targets the one derived table whose whole primary key is the referenced attribute. Zero matches, more
/// than one match, a key of another type, or a second reference to the same table each refuse the Transaction.
fn foreign_keys_of(
    owner: &DerivedTransaction,
    candidates: &[(&str, &Table)],
) -> Result<ResolvedKeys, String> {
    let root_index = owner
        .tables
        .iter()
        .position(|table| table.name.eq_ignore_ascii_case(&owner.name))
        .ok_or_else(|| "its root table was not derived".to_string())?;
    let root = &owner.tables[root_index];
    let mut links: Vec<(TableForeignKey, String)> = Vec::new();
    for reference in &owner.key_references {
        let column = root
            .columns
            .iter()
            .find(|c| c.name.eq_ignore_ascii_case(&reference.attribute))
            .ok_or_else(|| {
                format!(
                    "key '{}' is not a column of its root table",
                    reference.attribute
                )
            })?;
        let matching: Vec<&(&str, &Table)> = candidates
            .iter()
            .filter(|(_, table)| {
                matches!(
                    table.primary_key.as_slice(),
                    [only] if only.eq_ignore_ascii_case(&reference.references)
                )
            })
            .collect();
        let (target_txn, target) = match matching.as_slice() {
            [] => {
                return Err(format!(
                    "its primary key references {}, which is not the whole primary key of any derived table",
                    reference.references
                ));
            }
            [single] => (single.0, single.1),
            many => {
                let names: Vec<&str> = many.iter().map(|(_, table)| table.name.as_str()).collect();
                return Err(format!(
                    "its primary key references {}, which is the whole primary key of {} tables ({}), so the target is ambiguous",
                    reference.references,
                    many.len(),
                    names.join(", ")
                ));
            }
        };
        let target_column = target
            .columns
            .iter()
            .find(|c| c.name.eq_ignore_ascii_case(&reference.references))
            .ok_or_else(|| {
                format!(
                    "the referenced key {} is not a column of '{}'",
                    reference.references, target.name
                )
            })?;
        if target_column.data_type != column.data_type {
            return Err(format!(
                "its key '{}' has type {:?}, but the key it references, {} of '{}', has type {:?}",
                reference.attribute,
                column.data_type,
                reference.references,
                target.name,
                target_column.data_type
            ));
        }
        if links
            .iter()
            .any(|(fk, _)| fk.referenced_table.eq_ignore_ascii_case(&target.name))
        {
            return Err(format!(
                "two of its key references name table '{}'",
                target.name
            ));
        }
        links.push((
            TableForeignKey::new(
                format!("FK_{}_{}", root.name, target.name),
                vec![column.name.clone()],
                target.name.clone(),
                vec![target_column.name.clone()],
            ),
            target_txn.to_string(),
        ));
    }
    Ok((root_index, links))
}

/// Whether `start` reaches itself through the references: a cycle. `edges` holds upper-case Transaction names.
fn reaches_itself(start: &str, edges: &BTreeMap<String, Vec<String>>) -> bool {
    let mut stack: Vec<&str> = edges
        .get(start)
        .map(|next| next.iter().map(String::as_str).collect())
        .unwrap_or_default();
    let mut seen: BTreeSet<&str> = BTreeSet::new();
    while let Some(node) = stack.pop() {
        if node == start {
            return true;
        }
        if !seen.insert(node) {
            continue;
        }
        if let Some(next) = edges.get(node) {
            stack.extend(next.iter().map(String::as_str));
        }
    }
    false
}

/// Derives one Transaction's tables. A refused Transaction, or one with no STRUCTURE part, is an error that
/// names it; the refusal says why its tables cannot be derived.
fn derive_transaction_tables(
    obj: &KbObject,
    resolve_attr: &dyn Fn(&str) -> Option<DataType>,
) -> Result<DerivedTransaction, String> {
    let Some(structure_part) = obj.source_parts.iter().find(|p| p.name == "STRUCTURE") else {
        // A Transaction the reader refused holds no STRUCTURE part; its refusal names the element
        // that cannot be decoded, and is the reason its tables cannot be derived.
        return Err(match &obj.refusal {
            Some(reason) => format!(
                "Transaction '{}' is refused, so its tables cannot be derived: {reason}",
                obj.name
            ),
            None => format!(
                "Transaction '{}' requires a STRUCTURE part to derive tables",
                obj.name
            ),
        });
    };

    let structure: TransactionStructure = serde_json::from_str(&structure_part.source)
        .map_err(|e| format!("invalid Transaction STRUCTURE on '{}': {e}", obj.name))?;

    let tables = derive_tables_from_transaction(&structure, resolve_attr)
        .map_err(|e| format!("Transaction '{}': {e}", obj.name))?;
    Ok(DerivedTransaction {
        name: obj.name.clone(),
        tables,
        key_references: structure.key_references,
    })
}
