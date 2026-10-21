//! The web class a WebPanel or master page is emitted as, and the planner choosing between them.

use super::master::{MasterPage, plan_master_page};
use super::*;

/// One emitted web class.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WebClass {
    Plain(PlainWebPanel),
    Master(MasterPage),
    Prompt(crate::csharp::prompt::SelectionPrompt),
    NotAuthorized(crate::csharp::not_authorized::NotAuthorizedPanel),
    UnanimoMaster(crate::csharp::unanimo_master::UnanimoSidebarMaster),
    Transaction(crate::csharp::transaction_form::TransactionForm),
    WorkWith(crate::csharp::work_with::WorkWithClass),
}

impl WebClass {
    pub fn file_name(&self) -> String {
        match self {
            Self::Plain(panel) => panel.file_name(),
            Self::Master(master) => master.file_name(),
            Self::Prompt(prompt) => prompt.file_name(),
            Self::NotAuthorized(panel) => panel.file_name(),
            Self::UnanimoMaster(master) => master.file_name(),
            Self::Transaction(transaction) => transaction.file_name(),
            Self::WorkWith(class) => class.file_name(),
        }
    }

    /// The client script the SDK writes beside the class, as (file name, text).
    pub fn script(&self) -> Result<(String, String), String> {
        match self {
            Self::Plain(panel) => panel.script(),
            Self::Prompt(prompt) => prompt.script(),
            Self::Master(master) => master.script(),
            Self::NotAuthorized(panel) => panel.script(),
            Self::UnanimoMaster(master) => master.script(),
            Self::Transaction(transaction) => transaction.script(),
            Self::WorkWith(class) => class.script(),
        }
    }

    pub fn emit(&self, stamp: &str) -> String {
        match self {
            Self::Plain(panel) => panel.emit(stamp),
            Self::Master(master) => master.emit(stamp),
            Self::Prompt(prompt) => prompt.emit(stamp),
            Self::NotAuthorized(panel) => panel.emit(stamp),
            Self::UnanimoMaster(master) => master.emit(stamp),
            Self::Transaction(transaction) => transaction.emit(stamp),
            Self::WorkWith(class) => class.emit(stamp),
        }
    }
}

/// Plans one WebPanel: a master page in a matched shape, else a plain panel. `Err` names the reason.
pub fn plan_web_class(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<WebClass, String> {
    if object.kind == ObjectKind::WebPanel
        && settings_of(object)?.web_comp.as_deref() == Some("Master")
    {
        return match plan_master_page(object, env) {
            Ok(master) => Ok(WebClass::Master(master)),
            Err(reason) => {
                crate::csharp::unanimo_master::plan_unanimo_sidebar_master(object, catalog, env)
                    .map(WebClass::UnanimoMaster)
                    .map_err(|unanimo| {
                        format!("{reason}; as the Unanimo sidebar master: {unanimo}")
                    })
            }
        };
    }
    if object.kind == ObjectKind::WebPanel && settings_of(object)?.master_page_id.is_some() {
        let prompt_reason = match crate::csharp::prompt::plan_selection_prompt(object, catalog, env)
        {
            Ok(prompt) => return Ok(WebClass::Prompt(prompt)),
            Err(reason) => reason,
        };
        return Err(match plan_plain_webpanel(object, catalog, env) {
            Err(reason) => format!("{reason}; as a selection prompt: {prompt_reason}"),
            Ok(_) => prompt_reason,
        });
    }
    match plan_plain_webpanel(object, catalog, env) {
        Ok(panel) => Ok(WebClass::Plain(panel)),
        Err(plain_reason) => {
            let not_authorized =
                match crate::csharp::not_authorized::plan_not_authorized(object, catalog, env) {
                    Ok(panel) => return Ok(WebClass::NotAuthorized(panel)),
                    Err(reason) => reason,
                };
            crate::csharp::work_with::plan_work_with(object, catalog, env)
                .map(WebClass::WorkWith)
                .map_err(|reason| {
                    format!(
                        "{plain_reason}; as a not-authorized panel: {not_authorized}; as a Work With panel: {reason}"
                    )
                })
        }
    }
}
