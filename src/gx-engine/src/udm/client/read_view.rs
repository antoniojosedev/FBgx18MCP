//! Operation-local read views. Never inherit a catalog cache or share a SQL transaction.

use super::*;

impl UdmClient {
    /// Reads source and token metadata in one serializable SQL transaction. Dropping the
    /// private session rolls back its read-only transaction, on success and error alike.
    /// In-memory readers use a detached copy captured under the storage read lock.
    pub(crate) fn with_read_view<T>(
        &self,
        read: impl FnOnce(&Self) -> Result<T, String>,
    ) -> Result<T, String> {
        if self.consistent_read {
            return read(self);
        }
        let mut view = match &self.connection_info {
            Some(info) => {
                let mut view = Self::from_connection_info(info.clone());
                let session = live::ReadSession::start_consistent(info)?;
                view.read_session = Some(Arc::new(Mutex::new(Some(session))));
                view
            }
            None => {
                let snapshot = self
                    .storage
                    .read()
                    .map_err(|error| format!("Storage lock poisoned: {error}"))?
                    .clone();
                let mut view = Self::new_in_memory();
                view.storage = Arc::new(RwLock::new(snapshot));
                view
            }
        };
        view.design_model_id = self.design_model_id;
        view.consistent_read = true;
        read(&view)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn read_view_keeps_one_detached_storage_state_and_model() {
        let client = UdmClient::new_in_memory().with_design_model_id(4);
        client.storage.write().unwrap().next_entity_id = 7;
        client
            .with_read_view(|view| {
                assert_eq!(view.design_model_id(), 4);
                assert!(!Arc::ptr_eq(&view.storage, &client.storage));
                client.storage.write().unwrap().next_entity_id = 8;
                assert_eq!(view.storage.read().unwrap().next_entity_id, 7);
                view.with_read_view(|nested| {
                    assert!(Arc::ptr_eq(&nested.storage, &view.storage));
                    Ok(())
                })
            })
            .unwrap();
        client
            .with_read_view(|view| {
                assert_eq!(view.storage.read().unwrap().next_entity_id, 8);
                Ok(())
            })
            .unwrap();
    }

    #[test]
    fn read_views_do_not_inherit_or_publish_bulk_coverage() {
        let client = UdmClient::new_in_memory();
        client
            .bulk
            .write()
            .unwrap()
            .entity_versions
            .insert((1, 2, 3));
        let error = client
            .with_read_view(|view| {
                assert!(view.bulk.read().unwrap().entity_versions.is_empty());
                view.bulk.write().unwrap().entity_versions.insert((4, 5, 6));
                Err::<(), _>("refused".to_string())
            })
            .unwrap_err();
        assert_eq!(error, "refused");
        assert_eq!(client.bulk.read().unwrap().entity_versions.len(), 1);
    }

    #[test]
    #[ignore = "explicit read-only SQL integration on a verified disposable test KB"]
    fn live_read_view_uses_one_serializable_transaction_and_fails_closed() {
        let path = std::env::var("GXE_READ_VIEW_TEST_KB").expect("explicit disposable KB path");
        let path = std::path::PathBuf::from(path).canonicalize().unwrap();
        let parent = path.parent().unwrap().to_string_lossy().replace('\\', "/");
        assert!(parent.eq_ignore_ascii_case("//?/C:/KBs") || parent.eq_ignore_ascii_case("C:/KBs"));
        assert!(
            path.file_name()
                .unwrap()
                .to_string_lossy()
                .to_lowercase()
                .contains("teste")
        );
        let connection = path.join("knowledgebase.connection");
        let before = std::fs::read(&connection).unwrap();
        let client = UdmClient::connect_kb(&path).unwrap();
        client.with_read_view(|view| {
            for _ in 0..2 {
                let count = view.execute_live_sql("SET NOCOUNT ON; SELECT @@TRANCOUNT;")?;
                assert_eq!(count.trim(), "1");
                let isolation = view.execute_live_sql("SET NOCOUNT ON; SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID;")?;
                assert_eq!(isolation.trim(), "4");
            }
            assert!(view.execute_live_sql("SET NOCOUNT ON; SELECT * FROM __GXE_Absent_Read_View_Table;").is_err());
            let error = view.execute_live_sql("SET NOCOUNT ON; SELECT @@TRANCOUNT;").unwrap_err();
            assert!(error.contains("cannot be replayed"), "{error}");
            Ok(())
        }).unwrap();
        assert_eq!(std::fs::read(connection).unwrap(), before);
    }

    #[test]
    fn a_failed_consistent_session_cannot_restart_or_execute_a_write() {
        let info = KbConnectionInfo::parse_xml("<ConnectionInformation><ServerInstance>unused</ServerInstance><DBName>unused</DBName><IntegratedSecurity>true</IntegratedSecurity></ConnectionInformation>").unwrap();
        let mut view = UdmClient::from_connection_info(info);
        view.consistent_read = true;
        let error = view
            .execute_live_sql("SET NOCOUNT ON; SELECT 1;")
            .unwrap_err();
        assert!(error.contains("session is unavailable"), "{error}");
        view.read_session = Some(Arc::new(Mutex::new(None)));
        let error = view
            .execute_live_sql("SET NOCOUNT ON; SELECT 1;")
            .unwrap_err();
        assert!(error.contains("cannot be replayed"), "{error}");
        let error = view
            .execute_live_sql("DELETE FROM EntityVersion")
            .unwrap_err();
        assert!(error.contains("generated SELECT"), "{error}");
    }
}
