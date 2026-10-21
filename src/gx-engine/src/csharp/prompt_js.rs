//! The client script GeneXus 18 writes beside a default selection prompt (`gx0010.js` ...), from the
//! scope the prompt planner builds for its class (`csharp::prompt`).
//!
//! The text is matched, byte for byte, against the SDK's scripts for the seven KBTeste prompts (one to three
//! filters; numeric, `VarChar` and `Character` filters). Its line ends are the SDK's: CRLF, and CR CR LF
//! after `ip:[],` in each filter's edit control, which the SDK writes there.

use crate::csharp::template::{Fields, Value};

fn text<'a>(scope: &'a Fields, key: &str) -> Result<&'a str, String> {
    match scope.get(key) {
        Some(Value::Text(value)) => Ok(value),
        _ => Err(format!("prompt scope has no text '{key}'")),
    }
}

fn flag(scope: &Fields, key: &str) -> Result<bool, String> {
    match scope.get(key) {
        Some(Value::Flag(value)) => Ok(*value),
        _ => Err(format!("prompt scope has no flag '{key}'")),
    }
}

fn number(scope: &Fields, key: &str) -> Result<usize, String> {
    text(scope, key)?
        .parse()
        .map_err(|_| format!("prompt scope '{key}' is not a number"))
}

/// One filter of the prompt, with what the script writes for it.
struct Filter<'a> {
    f: &'a Fields,
    num: bool,
    /// The attribute's id, from its C# name (`A4TrnGroupProbeBaseId`).
    aid: String,
}

impl Filter<'_> {
    fn t(&self, key: &str) -> Result<&str, String> {
        text(self.f, key)
    }

    fn js_type(&self) -> Result<&'static str, String> {
        Ok(if self.num {
            "int"
        } else if flag(self.f, "vchar")? {
            "svchar"
        } else {
            "char"
        })
    }

    fn init(&self) -> &'static str {
        if self.num { "0" } else { "\"\"" }
    }
}

/// The script of the prompt whose class scope is `scope`, with the SDK's line ends.
pub fn prompt_script(scope: &Fields) -> Result<String, String> {
    let Some(Value::List(items)) = scope.get("filters") else {
        return Err("prompt scope has no filters".into());
    };
    let mut filters = Vec::new();
    for f in items {
        let aname = text(f, "aname")?;
        let aid: String = aname
            .strip_prefix('A')
            .unwrap_or("")
            .chars()
            .take_while(char::is_ascii_digit)
            .collect();
        if aid.is_empty() {
            return Err(format!("attribute '{aname}' has no id"));
        }
        filters.push(Filter {
            f,
            num: flag(f, "num")?,
            aid,
        });
    }
    let key = filters.first().ok_or("a prompt has at least one filter")?;
    let lname = text(scope, "lname")?;
    let lid = text(scope, "lid")?;
    let pav = text(scope, "pav")?;
    let pfld = format!("v{}", text(scope, "PUP")?);
    let ev_toggle = text(scope, "ev_toggle")?;
    let enter = number(scope, "ev_enter")?;
    let cancel = enter + 1;
    let grid = number(scope, "grid")?;
    let last = number(scope, "ctl_cancel")?;
    let toggle = number(scope, "ctl_toggle")?;
    let gxi = text(scope, "gxi")?;

    let mut o = String::new();
    o.push_str("gx.evt.autoSkip = false;\n");
    o.push_str(&format!("gx.define('{lname}', false, function () {{\n"));
    o.push_str(&format!("   this.ServerClass =  \"{lname}\" ;\n"));
    o.push_str("   this.PackageName =  \"GeneXus.Programs\" ;\n");
    o.push_str(&format!("   this.ServerFullClass =  \"{lname}.aspx\" ;\n"));
    o.push_str("   this.setObjectType(\"web\");\n   this.anyGridBaseTable = true;\n   this.hasEnterEvent = true;\n   this.skipOnEnter = false;\n   this.autoRefresh = true;\n   this.fullAjax = true;\n   this.supportAjaxEvents =  true ;\n   this.ajaxSecurityToken =  true ;\n");
    o.push_str(&format!("   this.DSO =  \"{}\" ;\n", text(scope, "theme")?));
    o.push_str(&format!(
        "   this.SetStandaloneVars=function()\n   {{\n      this.{pav}=gx.fn.getIntegerValue(\"{pfld}\",',') ;\n   }};\n"
    ));

    let client = |o: &mut String, ev: &str, body: &str| {
        o.push_str(&format!("   this.e{ev}{lid}1_client=function()\n   {{\n      return this.executeClientEvent(  function() {{\n         this.clearMessages();\n{body}         this.OnClientEventEnd();\n      }}, arguments);\n   }};\n"));
    };
    let toggle_out = r#"[{"av":"gx.fn.getCtrlProperty(\u0027ADVANCEDCONTAINER\u0027,\u0027Class\u0027)","ctrl":"ADVANCEDCONTAINER","prop":"Class"},{"ctrl":"BTNTOGGLE","prop":"Class"}]"#;
    client(
        &mut o,
        ev_toggle,
        &format!(
            "         if ( gx.text.compare( gx.fn.getCtrlProperty(\"ADVANCEDCONTAINER\",\"Class\") , \"AdvancedContainer\" ) == 0 )\n         {{\n            gx.fn.setCtrlProperty(\"ADVANCEDCONTAINER\",\"Class\", \"AdvancedContainer\"+\" \"+\"AdvancedContainerVisible\" );\n            gx.fn.setCtrlProperty(\"BTNTOGGLE\",\"Class\", gx.fn.getCtrlProperty(\"BTNTOGGLE\",\"Class\")+\" \"+\"BtnToggleActive\" );\n         }}\n         else\n         {{\n            gx.fn.setCtrlProperty(\"ADVANCEDCONTAINER\",\"Class\", \"AdvancedContainer\" );\n            gx.fn.setCtrlProperty(\"BTNTOGGLE\",\"Class\", \"BtnToggle\" );\n         }}\n         this.refreshOutputs({toggle_out});\n"
        ),
    );
    let label_out = |f: &Filter<'_>, visible: bool| -> Result<String, String> {
        let div = f.t("DIVUP")?;
        let class = format!(
            r#"{{"av":"gx.fn.getCtrlProperty(\u0027{div}\u0027,\u0027Class\u0027)","ctrl":"{div}","prop":"Class"}}"#
        );
        if !visible {
            return Ok(format!("[{class}]"));
        }
        let edit = f.t("VCUP")?;
        Ok(format!(
            r#"[{class},{{"av":"gx.fn.getCtrlProperty(\u0027{edit}\u0027,\u0027Visible\u0027)","ctrl":"{edit}","prop":"Visible"}}]"#
        ))
    };
    for f in &filters {
        let (div, edit) = (f.t("DIVUP")?, f.t("VCUP")?);
        client(
            &mut o,
            f.t("ev")?,
            &format!(
                "         if ( gx.text.compare( gx.fn.getCtrlProperty(\"{div}\",\"Class\") , \"AdvancedContainerItem\" ) == 0 )\n         {{\n            gx.fn.setCtrlProperty(\"{div}\",\"Class\", \"AdvancedContainerItem\"+\" \"+\"AdvancedContainerItemExpanded\" );\n            gx.fn.setCtrlProperty(\"{edit}\",\"Visible\", true );\n         }}\n         else\n         {{\n            gx.fn.setCtrlProperty(\"{div}\",\"Class\", \"AdvancedContainerItem\" );\n            gx.fn.setCtrlProperty(\"{edit}\",\"Visible\", false );\n         }}\n         this.refreshOutputs({});\n",
                label_out(f, true)?
            ),
        );
    }
    o.push_str(&format!("   this.e{enter}{lid}2_client=function()\n   {{\n      return this.executeServerEvent(\"ENTER\", true, arguments[0], false, false);\n   }};\n"));
    o.push_str(&format!("   this.e{cancel}{lid}1_client=function()\n   {{\n      return this.executeServerEvent(\"CANCEL\", true, null, false, false);\n   }};\n"));
    o.push_str("   this.GXValidFnc = [];\n   var GXValidFnc = this.GXValidFnc ;\n");
    let ids: Vec<String> = (2..=last)
        .filter(|id| *id != grid)
        .map(|id| id.to_string())
        .collect();
    o.push_str(&format!("   this.GXCtrlIds=[{}];\n", ids.join(",")));
    o.push_str(&format!("   this.GXLastCtrlId ={last};\n"));
    o.push_str(&format!("   this.Grid1Container = new gx.grid.grid(this, 2,\"WbpLvl2\",{grid},\"Grid1\",\"Grid1\",\"Grid1Container\",this.CmpContext,this.IsMasterPage,\"{lname}\",[],false,1,false,true,10,true,false,false,\"\",0,\"px\",0,\"px\",\"New row\",true,false,false,null,null,false,\"\",false,[1,1,1,1],false,0,true,false);\n"));
    o.push_str("   var Grid1Container = this.Grid1Container;\n");
    o.push_str(&format!("   Grid1Container.addBitmap(\"&Linkselection\",\"vLINKSELECTION\",{},0,\"px\",17,\"px\",null,\"\",\"\",\"SelectionAttribute\",\"WWActionColumn\");\n", grid + 1));
    for (index, f) in filters.iter().enumerate() {
        let len = f.t("len")?;
        o.push_str(&format!(
            "   Grid1Container.addSingleLineEdit({aid},{ctl},\"{up}\",\"{ctx}\",\"\",\"{n}\",\"{ty}\",0,\"px\",{len},{len},\"{align}\",null,[],{aid},\"{n}\",true,0,false,false,\"{ro}\",0,\"{col}\");\n",
            aid = f.aid,
            ctl = grid + 2 + index,
            up = f.t("UP")?,
            ctx = f.t("ctx")?,
            n = f.t("N")?,
            ty = f.js_type()?,
            align = f.t("align")?,
            ro = f.t("rocls")?,
            col = f.t("colcls")?,
        ));
    }
    o.push_str("   this.Grid1Container.emptyText = \"\";\n   this.setGrid(Grid1Container);\n");

    let blank = |o: &mut String, id: usize| {
        o.push_str(&format!(
            "   GXValidFnc[{id}]={{ id: {id}, fld:\"\",grid:0}};\n"
        ));
    };
    let named = |o: &mut String, id: usize, fld: &str, extra: &str| {
        o.push_str(&format!(
            "   GXValidFnc[{id}]={{ id: {id}, fld:\"{fld}\",grid:0{extra}}};\n"
        ));
    };
    blank(&mut o, 2);
    named(&mut o, 3, "MAIN", "");
    blank(&mut o, 4);
    blank(&mut o, 5);
    named(&mut o, 6, "ADVANCEDCONTAINER", "");
    for f in &filters {
        let c: usize = f
            .t("ctl")?
            .parse()
            .map_err(|_| "a filter control id is not a number".to_string())?;
        if c < 9 {
            return Err(format!(
                "filter control id {c} is below the layout's first filter"
            ));
        }
        blank(&mut o, c - 9);
        blank(&mut o, c - 8);
        named(&mut o, c - 7, f.t("DIVUP")?, "");
        blank(&mut o, c - 6);
        blank(&mut o, c - 5);
        o.push_str(&format!(
            "   GXValidFnc[{id}]={{ id: {id}, fld:\"{fld}\", format:1,grid:0,evt:\"e{ev}{lid}1_client\", ctrltype: \"textblock\"}};\n",
            id = c - 4,
            fld = f.t("LBLUP")?,
            ev = f.t("ev")?
        ));
        blank(&mut o, c - 3);
        blank(&mut o, c - 2);
        blank(&mut o, c - 1);
        let (av, fld) = (f.t("cav")?, f.t("VCUP")?);
        let (z, old) = (format!("Z{}", &av[1..]), format!("O{}", &av[1..]));
        let pic = if f.num {
            format!(",pic:\"{}\"", f.t("pic")?)
        } else {
            String::new()
        };
        let (value, this_val, val) = if f.num {
            (
                "gx.num.intval(Value)".to_string(),
                "gx.num.intval(this.val())".to_string(),
                format!("gx.fn.getIntegerValue(\"{fld}\",',')"),
            )
        } else {
            (
                "Value".to_string(),
                "this.val()".to_string(),
                format!("gx.fn.getControlValue(\"{fld}\")"),
            )
        };
        o.push_str(&format!(
            "   GXValidFnc[{c}]={{ id:{c} ,lvl:0,type:\"{ty}\",len:{len},dec:0,sign:false{pic},ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.Grid1Container],fld:\"{fld}\",fmt:0,gxz:\"{z}\",gxold:\"{old}\",gxvar:\"{av}\",ucs:[],op:[],ip:[],\r\n",
            ty = f.js_type()?,
            len = f.t("len")?
        ));
        o.push_str(&format!(
            "\t\t\t\t\t\tnacdep:[],ctrltype:\"edit\",v2v:function(Value){{if(Value!==undefined)gx.O.{av}={value}}},v2z:function(Value){{if(Value!==undefined)gx.O.{z}={value}}},v2c:function(){{gx.fn.setControlValue(\"{fld}\",gx.O.{av},0)}},c2v:function(){{if(this.val()!==undefined)gx.O.{av}={this_val}}},val:function(){{return {val}}},nac:gx.falseFn}};\n"
        ));
    }
    let last_filter: usize = filters
        .last()
        .map(|f| f.t("ctl"))
        .transpose()?
        .unwrap_or("0")
        .parse()
        .map_err(|_| "a filter control id is not a number".to_string())?;
    blank(&mut o, last_filter + 1);
    named(&mut o, last_filter + 2, "GRIDTABLE", "");
    for id in last_filter + 3..toggle {
        blank(&mut o, id);
    }
    named(
        &mut o,
        toggle,
        "BTNTOGGLE",
        &format!(",evt:\"e{ev_toggle}{lid}1_client\""),
    );
    for id in toggle + 1..grid {
        blank(&mut o, id);
    }
    let g = grid;
    o.push_str(&format!(
        "   GXValidFnc[{id}]={{ id:{id} ,lvl:2,type:\"bits\",len:1024,dec:0,sign:false,ro:1,isacc:0,grid:{g},gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:\"vLINKSELECTION\",fmt:0,gxz:\"ZV5LinkSelection\",gxold:\"OV5LinkSelection\",gxvar:\"AV5LinkSelection\",ucs:[],op:[],ip:[],nacdep:[],ctrltype:\"edit\",inputType:'text',v2v:function(Value){{if(Value!==undefined)gx.O.AV5LinkSelection=Value}},v2z:function(Value){{if(Value!==undefined)gx.O.ZV5LinkSelection=Value}},v2c:function(row){{gx.fn.setGridMultimediaValue(\"vLINKSELECTION\",row || gx.fn.currentGridRowImpl({g}),gx.O.AV5LinkSelection,gx.O.AV{gxi}Linkselection_GXI)}},c2v:function(row){{gx.O.AV{gxi}Linkselection_GXI=this.val_GXI();if(this.val(row)!==undefined)gx.O.AV5LinkSelection=this.val(row)}},val:function(row){{return gx.fn.getGridControlValue(\"vLINKSELECTION\",row || gx.fn.currentGridRowImpl({g}))}},val_GXI:function(row){{return gx.fn.getGridControlValue(\"vLINKSELECTION_GXI\",row || gx.fn.currentGridRowImpl({g}))}}, gxvar_GXI:'AV{gxi}Linkselection_GXI',nac:gx.falseFn}};\n",
        id = g + 1
    ));
    for (index, f) in filters.iter().enumerate() {
        let c = g + 2 + index;
        let (av, fld) = (f.t("aname")?, f.t("UP")?);
        let (z, old) = (format!("Z{}", &av[1..]), format!("O{}", &av[1..]));
        let pic = if f.num {
            format!(",pic:\"{}\"", f.t("pic")?)
        } else {
            String::new()
        };
        let correct = if f.num { "" } else { "autoCorrect:\"1\"," };
        let (value, row_val, val) = if f.num {
            (
                "gx.num.intval(Value)".to_string(),
                "gx.num.intval(this.val(row))".to_string(),
                format!(
                    "gx.fn.getGridIntegerValue(\"{fld}\",row || gx.fn.currentGridRowImpl({g}),',')"
                ),
            )
        } else {
            (
                "Value".to_string(),
                "this.val(row)".to_string(),
                format!(
                    "gx.fn.getGridControlValue(\"{fld}\",row || gx.fn.currentGridRowImpl({g}))"
                ),
            )
        };
        o.push_str(&format!(
            "   GXValidFnc[{c}]={{ id:{c} ,lvl:2,type:\"{ty}\",len:{len},dec:0,sign:false{pic},ro:1,isacc:0,grid:{g},gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:\"{fld}\",fmt:0,gxz:\"{z}\",gxold:\"{old}\",gxvar:\"{av}\",ucs:[],op:[],ip:[],nacdep:[],ctrltype:\"edit\",inputType:'text',{correct}v2v:function(Value){{if(Value!==undefined)gx.O.{av}={value}}},v2z:function(Value){{if(Value!==undefined)gx.O.{z}={value}}},v2c:function(row){{gx.fn.setGridControlValue(\"{fld}\",row || gx.fn.currentGridRowImpl({g}),gx.O.{av},0)}},c2v:function(row){{if(this.val(row)!==undefined)gx.O.{av}={row_val}}},val:function(row){{return {val}}},nac:gx.falseFn}};\n",
            ty = f.js_type()?,
            len = f.t("len")?
        ));
    }
    for id in g + 2 + filters.len()..last {
        blank(&mut o, id);
    }
    named(
        &mut o,
        last,
        "BTN_CANCEL",
        &format!(",evt:\"e{cancel}{lid}1_client\""),
    );
    for f in &filters {
        let av = f.t("cav")?;
        for name in [
            av.to_string(),
            format!("Z{}", &av[1..]),
            format!("O{}", &av[1..]),
        ] {
            o.push_str(&format!("   this.{name} = {} ;\n", f.init()));
        }
    }
    o.push_str("   this.ZV5LinkSelection = \"\" ;\n   this.OV5LinkSelection = \"\" ;\n");
    for f in &filters {
        let av = f.t("aname")?;
        for name in [format!("Z{}", &av[1..]), format!("O{}", &av[1..])] {
            o.push_str(&format!("   this.{name} = {} ;\n", f.init()));
        }
    }
    for f in &filters {
        o.push_str(&format!("   this.{} = {} ;\n", f.t("cav")?, f.init()));
    }
    o.push_str(&format!(
        "   this.{pav} = 0 ;\n   this.AV5LinkSelection = \"\" ;\n"
    ));
    for f in &filters {
        o.push_str(&format!("   this.{} = {} ;\n", f.t("aname")?, f.init()));
    }
    let mut events = vec![
        format!("\"e{enter}{lid}2_client\": [\"ENTER\", true]"),
        format!("\"e{cancel}{lid}1_client\": [\"CANCEL\", true]"),
        format!("\"e{ev_toggle}{lid}1_client\": [\"'TOGGLE'\", false]"),
    ];
    for f in &filters {
        events.push(format!(
            "\"e{}{lid}1_client\": [\"{}.CLICK\", false]",
            f.t("ev")?,
            f.t("LBLUP")?
        ));
    }
    o.push_str(&format!("   this.Events = {{{}}};\n", events.join(" ,")));
    let mut parms = Vec::new();
    for f in &filters {
        parms.push(if f.num {
            format!(
                "{{\"av\":\"{}\",\"fld\":\"{}\",\"pic\":\"{}\"}}",
                f.t("cav")?,
                f.t("VCUP")?,
                f.t("pic")?
            )
        } else {
            format!(
                "{{\"av\":\"{}\",\"fld\":\"{}\"}}",
                f.t("cav")?,
                f.t("VCUP")?
            )
        });
    }
    let refresh = format!(
        "[[{{\"av\":\"GRID1_nFirstRecordOnPage\"}},{{\"av\":\"GRID1_nEOF\"}},{{\"av\":\"\",\"ctrl\":\"GRID1\",\"prop\":\"Rows\"}},{}],[]]",
        parms.join(",")
    );
    o.push_str(&format!("   this.EvtParms[\"REFRESH\"] = {refresh};\n"));
    o.push_str(&format!(
        "   this.EvtParms[\"'TOGGLE'\"] = [{toggle_out},{toggle_out}];\n"
    ));
    for f in &filters {
        o.push_str(&format!(
            "   this.EvtParms[\"{}.CLICK\"] = [{},{}];\n",
            f.t("LBLUP")?,
            label_out(f, false)?,
            label_out(f, true)?
        ));
    }
    if !key.num {
        return Err("the prompt's key filter is not numeric".into());
    }
    let key_pic = key.t("pic")?;
    o.push_str(&format!(
        "   this.EvtParms[\"ENTER\"] = [[{{\"av\":\"{}\",\"fld\":\"{}\",\"pic\":\"{key_pic}\",\"hsh\":true}}],[{{\"av\":\"{pav}\",\"fld\":\"{pfld}\",\"pic\":\"{key_pic}\"}}]];\n",
        key.t("aname")?,
        key.t("UP")?
    ));
    for page in ["FIRSTPAGE", "PREVPAGE", "NEXTPAGE", "LASTPAGE"] {
        o.push_str(&format!(
            "   this.EvtParms[\"GRID1_{page}\"] = {refresh};\n"
        ));
    }
    o.push_str(&format!(
        "   this.setVCMap(\"{pav}\", \"{pfld}\", 0, \"int\", {}, 0);\n",
        key.t("len")?
    ));
    o.push_str("   Grid1Container.addRefreshingParm({rfrProp:\"Rows\", gxGrid:\"Grid1\"});\n");
    for f in &filters {
        o.push_str(&format!(
            "   Grid1Container.addRefreshingVar(this.GXValidFnc[{}]);\n",
            f.t("ctl")?
        ));
    }
    for f in &filters {
        o.push_str(&format!(
            "   Grid1Container.addRefreshingParm(this.GXValidFnc[{}]);\n",
            f.t("ctl")?
        ));
    }
    o.push_str("   this.Initialize( );\n});\n");
    o.push_str(&format!(
        "gx.wi( function() {{ gx.createParentObj(this.{lname});}});\n"
    ));
    Ok(o.replace('\n', "\r\n"))
}
