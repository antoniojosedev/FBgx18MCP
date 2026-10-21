//! The client script GeneXus 18 writes beside a Transaction's web class (`livemoveattribute.js` ...), from
//! the scope the Transaction planner builds for its class (`csharp::transaction_form`).
//!
//! The text is matched, byte for byte, against the SDK's scripts for the seven KBTeste Transactions the
//! planner admits: custom forms of `Character(20)` attributes (with hidden attributes and user events
//! showing a literal message), the default form of a `Numeric(8)` and a `VarChar(40)` attribute, and the
//! key-only subtype and supertype forms. Its line ends are the SDK's: CRLF, and CR CR LF after `ip:[...],`
//! in each attribute's edit control.

use crate::csharp::template::{Fields, Value};

/// The shape of the matched form.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Shape {
    Custom,
    Default,
    KeyOnly,
}

fn text<'a>(scope: &'a Fields, key: &str) -> Result<&'a str, String> {
    match scope.get(key) {
        Some(Value::Text(value)) => Ok(value),
        _ => Err(format!("transaction scope has no text '{key}'")),
    }
}

fn list<'a>(scope: &'a Fields, key: &str) -> &'a [Fields] {
    match scope.get(key) {
        Some(Value::List(items)) => items,
        _ => &[],
    }
}

fn number(scope: &Fields, key: &str) -> Result<usize, String> {
    text(scope, key)?
        .parse()
        .map_err(|_| format!("transaction scope '{key}' is not a number"))
}

/// The client kind of an attribute: script type, length and numeric picture.
#[derive(Clone, Copy)]
struct Kind {
    js: &'static str,
    len: usize,
    pic: Option<&'static str>,
}

const KEY: Kind = Kind {
    js: "int",
    len: 4,
    pic: Some("ZZZ9"),
};
const INT8: Kind = Kind {
    js: "int",
    len: 8,
    pic: Some("ZZZZZZZ9"),
};
const CHAR20: Kind = Kind {
    js: "char",
    len: 20,
    pic: None,
};
const SVCHAR40: Kind = Kind {
    js: "svchar",
    len: 40,
    pic: None,
};

/// One attribute as the script names it.
struct Attr {
    id: String,
    p: String,
    upper: String,
    focus: usize,
    kind: Kind,
}

impl Attr {
    fn of(f: &Fields, kind: Kind, focus: Option<usize>) -> Result<Self, String> {
        let focus = match focus {
            Some(focus) => focus,
            None => match f.get("focus") {
                Some(Value::Text(v)) => v.parse().map_err(|_| "focus is not a number")?,
                _ => 0,
            },
        };
        Ok(Self {
            id: text(f, "id")?.to_string(),
            p: text(f, "p")?.to_string(),
            upper: text(f, "U")?.to_string(),
            focus,
            kind,
        })
    }

    fn av(&self) -> String {
        format!("A{}{}", self.id, self.p)
    }

    fn init(&self) -> &'static str {
        if self.kind.js == "int" { "0" } else { "\"\"" }
    }

    fn parm(&self) -> String {
        match self.kind.pic {
            Some(pic) => format!(
                "{{\"av\":\"{}\",\"fld\":\"{}\",\"pic\":\"{pic}\"}}",
                self.av(),
                self.upper
            ),
            None => format!("{{\"av\":\"{}\",\"fld\":\"{}\"}}", self.av(), self.upper),
        }
    }
}

fn blank(o: &mut String, i: usize) {
    o.push_str(&format!(
        "   GXValidFnc[{i}]={{ id: {i}, fld:\"\",grid:0}};\n"
    ));
}

fn named(o: &mut String, i: usize, fld: &str, extra: &str) {
    o.push_str(&format!(
        "   GXValidFnc[{i}]={{ id: {i}, fld:\"{fld}\",grid:0{extra}}};\n"
    ));
}

fn edit(o: &mut String, ctl: usize, a: &Attr, fnc: &str, op: &str, ip: &str) {
    let av = a.av();
    let z = format!("Z{}", &av[1..]);
    let old = format!("O{}", &av[1..]);
    let fld = &a.upper;
    let num = a.kind.js == "int";
    let (typ, val) = if num {
        (
            format!(
                "type:\"int\",len:{},dec:0,sign:false,pic:\"{}\"",
                a.kind.len,
                a.kind.pic.unwrap_or("")
            ),
            format!("gx.fn.getIntegerValue(\"{fld}\",',')"),
        )
    } else {
        (
            format!("type:\"{}\",len:{},dec:0,sign:false", a.kind.js, a.kind.len),
            format!("gx.fn.getControlValue(\"{fld}\")"),
        )
    };
    let conv = |x: &str| {
        if num {
            format!("gx.num.intval({x})")
        } else {
            x.to_string()
        }
    };
    o.push_str(&format!(
        "   GXValidFnc[{ctl}]={{ id:{ctl} ,lvl:0,{typ},ro:0,grid:0,gxgrid:null,fnc:{fnc},isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:\"{fld}\",fmt:0,gxz:\"{z}\",gxold:\"{old}\",gxvar:\"{av}\",ucs:[],op:[{op}],ip:[{ip}],\r\n"
    ));
    o.push_str(&format!(
        "\t\t\t\t\t\tnacdep:[],ctrltype:\"edit\",v2v:function(Value){{if(Value!==undefined)gx.O.{av}={}}},v2z:function(Value){{if(Value!==undefined)gx.O.{z}={}}},v2c:function(){{gx.fn.setControlValue(\"{fld}\",gx.O.{av},0)}},c2v:function(){{if(this.val()!==undefined)gx.O.{av}={}}},val:function(){{return {val}}},nac:gx.falseFn}};\n",
        conv("Value"),
        conv("Value"),
        conv("this.val()")
    ));
}

/// The script's file name and bytes.
pub fn script(
    name: &str,
    shape: Shape,
    scope: &Fields,
    events: &[(String, String)],
) -> Result<(String, String), String> {
    let lname = text(scope, "lname")?;
    let oid = text(scope, "oid")?.to_ascii_lowercase();
    let tpk = text(scope, "tpk")?;
    let theme = text(scope, "theme")?;
    let key = Attr {
        id: text(scope, "k_id")?.to_string(),
        p: text(scope, "k_p")?.to_string(),
        upper: text(scope, "k_U")?.to_string(),
        focus: 34,
        kind: KEY,
    };
    let (fa, sa, ha, buttons) = match shape {
        Shape::Custom => {
            let attrs = |key: &str| -> Result<Vec<Attr>, String> {
                list(scope, key)
                    .iter()
                    .map(|f| Attr::of(f, CHAR20, None))
                    .collect()
            };
            let buttons = [
                number(scope, "btn1")?,
                number(scope, "btn2")?,
                number(scope, "btn3")?,
            ];
            (attrs("fa")?, attrs("sa")?, attrs("ha")?, buttons)
        }
        Shape::Default => {
            let slot = |slot: &str, kind: Kind, focus: usize| -> Result<Attr, String> {
                Ok(Attr {
                    id: text(scope, &format!("{slot}_id"))?.to_string(),
                    p: text(scope, &format!("{slot}_p"))?.to_string(),
                    upper: text(scope, &format!("{slot}_U"))?.to_string(),
                    focus,
                    kind,
                })
            };
            let fa = vec![slot("a1", INT8, 39)?, slot("a2", SVCHAR40, 44)?];
            let sa = vec![slot("a1", INT8, 39)?, slot("a2", SVCHAR40, 44)?];
            (fa, sa, Vec::new(), [49, 51, 53])
        }
        Shape::KeyOnly => (Vec::new(), Vec::new(), Vec::new(), [39, 41, 43]),
    };
    let prompt = shape == Shape::KeyOnly && scope.contains_key("group");
    let n = events.len();
    let ev = |i: usize| format!("e{i}{oid}{tpk}_client");

    let mut o = String::new();
    o.push_str("gx.evt.autoSkip = false;\n");
    o.push_str(&format!("gx.define('{lname}', false, function () {{\n"));
    o.push_str(&format!(
        "   this.ServerClass =  \"{lname}\" ;\n   this.PackageName =  \"GeneXus.Programs\" ;\n   this.ServerFullClass =  \"{lname}.aspx\" ;\n"
    ));
    o.push_str("   this.setObjectType(\"trn\");\n   this.hasEnterEvent = true;\n   this.skipOnEnter = false;\n   this.fullAjax = true;\n   this.supportAjaxEvents =  true ;\n   this.ajaxSecurityToken =  true ;\n");
    o.push_str(&format!("   this.DSO =  \"{theme}\" ;\n"));
    o.push_str("   this.SetStandaloneVars=function()\n   {\n");
    for a in &ha {
        o.push_str(&format!(
            "      this.{}=gx.fn.getControlValue(\"{}\") ;\n",
            a.av(),
            a.upper
        ));
    }
    o.push_str("   };\n");
    let valid = format!("Valid_{}", text(scope, "k_cap")?);
    o.push_str(&format!(
        "   this.{valid}=function()\n   {{\n      return this.validSrvEvt(\"{valid}\", 0).then((function (ret) {{\n      return ret;\n      }}).closure(this));\n   }}\n"
    ));
    for (i, (_, message)) in events.iter().enumerate() {
        o.push_str(&format!(
            "   this.{}=function()\n   {{\n      return this.executeClientEvent(  function() {{\n         this.clearMessages();\n         this.addMessage(\"{message}\");\n         this.refreshOutputs([]);\n         this.OnClientEventEnd();\n      }}, arguments);\n   }};\n",
            ev(11 + i)
        ));
    }
    for (offset, event) in [(11, "ENTER"), (12, "CANCEL")] {
        o.push_str(&format!(
            "   this.{}=function()\n   {{\n      return this.executeServerEvent(\"{event}\", true, null, false, false);\n   }};\n",
            ev(offset + n)
        ));
    }
    o.push_str("   this.GXValidFnc = [];\n   var GXValidFnc = this.GXValidFnc ;\n");
    let [b1, b2, b3] = buttons;
    let last = b3 + usize::from(prompt);
    let ids: Vec<String> = (2..=last)
        .filter(|i| *i != 12)
        .map(|i| i.to_string())
        .collect();
    o.push_str(&format!(
        "   this.GXCtrlIds=[{}];\n   this.GXLastCtrlId ={last};\n",
        ids.join(",")
    ));
    blank(&mut o, 2);
    named(&mut o, 3, "MAINTABLE", "");
    blank(&mut o, 4);
    blank(&mut o, 5);
    named(&mut o, 6, "TITLECONTAINER", "");
    blank(&mut o, 7);
    blank(&mut o, 8);
    o.push_str(
        "   GXValidFnc[9]={ id: 9, fld:\"TITLE\", format:0,grid:0, ctrltype: \"textblock\"};\n",
    );
    for i in [10, 11, 13, 14] {
        blank(&mut o, i);
    }
    named(&mut o, 15, "FORMCONTAINER", "");
    blank(&mut o, 16);
    named(&mut o, 17, "TOOLBARCELL", "");
    for i in [18, 19, 20] {
        blank(&mut o, i);
    }
    for (j, (ctl, btn, std)) in [
        (21, "BTN_FIRST", "FIRST"),
        (23, "BTN_PREVIOUS", "PREVIOUS"),
        (25, "BTN_NEXT", "NEXT"),
        (27, "BTN_LAST", "LAST"),
        (29, "BTN_SELECT", "SELECT"),
    ]
    .into_iter()
    .enumerate()
    {
        named(
            &mut o,
            ctl,
            btn,
            &format!(",evt:\"{}\",std:\"{std}\"", ev(13 + n + j)),
        );
        blank(&mut o, ctl + 1);
    }
    for i in [31, 32, 33] {
        blank(&mut o, i);
    }
    // The key's edit lists the form's attributes by focus: in form order on a custom form,
    // reversed on the default form.
    let mut focus: Vec<String> = fa.iter().map(|a| a.focus.to_string()).collect();
    if shape == Shape::Default {
        focus.reverse();
    }
    let op = focus.join(",");
    focus.push("34".to_string());
    edit(
        &mut o,
        34,
        &key,
        &format!("this.{valid}"),
        &op,
        &focus.join(","),
    );
    let mut prev = 34;
    for a in &fa {
        for i in prev + 1..a.focus {
            blank(&mut o, i);
        }
        edit(&mut o, a.focus, a, "null", "", "");
        prev = a.focus;
    }
    for i in prev + 1..b1 {
        blank(&mut o, i);
    }
    named(
        &mut o,
        b1,
        "BTN_ENTER",
        &format!(",evt:\"{}\",std:\"ENTER\"", ev(11 + n)),
    );
    blank(&mut o, b1 + 1);
    named(
        &mut o,
        b2,
        "BTN_CANCEL",
        &format!(",evt:\"{}\"", ev(12 + n)),
    );
    blank(&mut o, b2 + 1);
    named(
        &mut o,
        b3,
        "BTN_DELETE",
        &format!(",evt:\"{}\",std:\"DELETE\"", ev(18 + n)),
    );
    if prompt {
        o.push_str(&format!(
            "   GXValidFnc[{last}]={{ id: {last}, fld:\"PROMPT_{}\",grid:4}};\n",
            key.id
        ));
    }
    for a in std::iter::once(&key).chain(&fa) {
        for prefix in ["A", "Z", "O"] {
            o.push_str(&format!(
                "   this.{prefix}{}{} = {} ;\n",
                a.id,
                a.p,
                a.init()
            ));
        }
    }
    o.push_str(&format!("   this.{} = 0 ;\n", key.av()));
    for a in &sa {
        o.push_str(&format!("   this.{} = {} ;\n", a.av(), a.init()));
    }
    let mut evs = vec![
        format!("\"{}\": [\"ENTER\", true]", ev(11 + n)),
        format!("\"{}\": [\"CANCEL\", true]", ev(12 + n)),
    ];
    for (i, (event, _)) in events.iter().enumerate() {
        evs.push(format!("\"{}\": [\"'{event}'\", false]", ev(11 + i)));
    }
    o.push_str(&format!("   this.Events = {{{}}};\n", evs.join(" ,")));
    o.push_str("   this.EvtParms[\"ENTER\"] = [[{\"postForm\":true}],[]];\n");
    let hidden: Vec<String> = ha
        .iter()
        .map(|a| format!("{{\"av\":\"{}\",\"fld\":\"{}\"}}", a.av(), a.upper))
        .collect();
    let hidden = hidden.join(",");
    o.push_str(&format!(
        "   this.EvtParms[\"REFRESH\"] = [[{hidden}],[]];\n"
    ));
    for (event, _) in events {
        o.push_str(&format!("   this.EvtParms[\"'{event}'\"] = [[],[]];\n"));
    }
    let mode = "{\"av\":\"Gx_mode\",\"fld\":\"vMODE\",\"pic\":\"@!\"}";
    let mut ins: Vec<String> = Vec::new();
    if !hidden.is_empty() {
        ins.push(hidden.clone());
    }
    ins.push(key.parm());
    ins.push(mode.to_string());
    let mut outs: Vec<String> = sa.iter().map(Attr::parm).collect();
    outs.push(mode.to_string());
    for a in std::iter::once(&key).chain(&sa) {
        outs.push(format!("{{\"av\":\"Z{}{}\"}}", a.id, a.p));
    }
    outs.push("{\"ctrl\":\"BTN_DELETE\",\"prop\":\"Enabled\"}".to_string());
    outs.push("{\"ctrl\":\"BTN_ENTER\",\"prop\":\"Enabled\"}".to_string());
    o.push_str(&format!(
        "   this.EvtParms[\"VALID_{}\"] = [[{}],[{}]];\n",
        key.upper,
        ins.join(","),
        outs.join(",")
    ));
    if prompt {
        o.push_str(&format!(
            "   this.setPrompt(\"PROMPT_{}\", [34]);\n",
            key.id
        ));
    }
    o.push_str("   this.EnterCtrl = [\"BTN_ENTER\"];\n");
    for a in &ha {
        o.push_str(&format!(
            "   this.setVCMap(\"{}\", \"{}\", 0, \"char\", 20, 0);\n",
            a.av(),
            a.upper
        ));
    }
    o.push_str("   this.Initialize( );\n});\n");
    o.push_str(&format!(
        "gx.wi( function() {{ gx.createParentObj(this.{lname});}});\n"
    ));
    Ok((
        format!("{}.js", name.to_ascii_lowercase()),
        o.replace('\n', "\r\n"),
    ))
}
