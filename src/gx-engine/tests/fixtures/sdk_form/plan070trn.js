gx.evt.autoSkip = false;
gx.define('plan070trn', false, function () {
   this.ServerClass =  "plan070trn" ;
   this.PackageName =  "GeneXus.Programs" ;
   this.ServerFullClass =  "plan070trn.aspx" ;
   this.setObjectType("trn");
   this.hasEnterEvent = true;
   this.skipOnEnter = false;
   this.fullAjax = true;
   this.supportAjaxEvents =  true ;
   this.ajaxSecurityToken =  true ;
   this.DSO =  "KBTeste" ;
   this.SetStandaloneVars=function()
   {
   };
   this.Valid_Plan070trnid=function()
   {
      return this.validSrvEvt("Valid_Plan070trnid", 0).then((function (ret) {
      return ret;
      }).closure(this));
   }
   this.e11054_client=function()
   {
      return this.executeServerEvent("ENTER", true, null, false, false);
   };
   this.e12054_client=function()
   {
      return this.executeServerEvent("CANCEL", true, null, false, false);
   };
   this.GXValidFnc = [];
   var GXValidFnc = this.GXValidFnc ;
   this.GXCtrlIds=[2,3,4,5,6,7,8,9,10,11,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44];
   this.GXLastCtrlId =44;
   GXValidFnc[2]={ id: 2, fld:"",grid:0};
   GXValidFnc[3]={ id: 3, fld:"MAINTABLE",grid:0};
   GXValidFnc[4]={ id: 4, fld:"",grid:0};
   GXValidFnc[5]={ id: 5, fld:"",grid:0};
   GXValidFnc[6]={ id: 6, fld:"TITLECONTAINER",grid:0};
   GXValidFnc[7]={ id: 7, fld:"",grid:0};
   GXValidFnc[8]={ id: 8, fld:"",grid:0};
   GXValidFnc[9]={ id: 9, fld:"TITLE", format:0,grid:0, ctrltype: "textblock"};
   GXValidFnc[10]={ id: 10, fld:"",grid:0};
   GXValidFnc[11]={ id: 11, fld:"",grid:0};
   GXValidFnc[13]={ id: 13, fld:"",grid:0};
   GXValidFnc[14]={ id: 14, fld:"",grid:0};
   GXValidFnc[15]={ id: 15, fld:"FORMCONTAINER",grid:0};
   GXValidFnc[16]={ id: 16, fld:"",grid:0};
   GXValidFnc[17]={ id: 17, fld:"TOOLBARCELL",grid:0};
   GXValidFnc[18]={ id: 18, fld:"",grid:0};
   GXValidFnc[19]={ id: 19, fld:"",grid:0};
   GXValidFnc[20]={ id: 20, fld:"",grid:0};
   GXValidFnc[21]={ id: 21, fld:"BTN_FIRST",grid:0,evt:"e13054_client",std:"FIRST"};
   GXValidFnc[22]={ id: 22, fld:"",grid:0};
   GXValidFnc[23]={ id: 23, fld:"BTN_PREVIOUS",grid:0,evt:"e14054_client",std:"PREVIOUS"};
   GXValidFnc[24]={ id: 24, fld:"",grid:0};
   GXValidFnc[25]={ id: 25, fld:"BTN_NEXT",grid:0,evt:"e15054_client",std:"NEXT"};
   GXValidFnc[26]={ id: 26, fld:"",grid:0};
   GXValidFnc[27]={ id: 27, fld:"BTN_LAST",grid:0,evt:"e16054_client",std:"LAST"};
   GXValidFnc[28]={ id: 28, fld:"",grid:0};
   GXValidFnc[29]={ id: 29, fld:"BTN_SELECT",grid:0,evt:"e17054_client",std:"SELECT"};
   GXValidFnc[30]={ id: 30, fld:"",grid:0};
   GXValidFnc[31]={ id: 31, fld:"",grid:0};
   GXValidFnc[32]={ id: 32, fld:"",grid:0};
   GXValidFnc[33]={ id: 33, fld:"",grid:0};
   GXValidFnc[34]={ id:34 ,lvl:0,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:0,grid:0,gxgrid:null,fnc:this.Valid_Plan070trnid,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"PLAN070TRNID",fmt:0,gxz:"Z11Plan070TrnId",gxold:"O11Plan070TrnId",gxvar:"A11Plan070TrnId",ucs:[],op:[],ip:[34],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.A11Plan070TrnId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z11Plan070TrnId=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("PLAN070TRNID",gx.O.A11Plan070TrnId,0)},c2v:function(){if(this.val()!==undefined)gx.O.A11Plan070TrnId=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("PLAN070TRNID",',')},nac:gx.falseFn};
   GXValidFnc[35]={ id: 35, fld:"",grid:0};
   GXValidFnc[36]={ id: 36, fld:"",grid:0};
   GXValidFnc[37]={ id: 37, fld:"",grid:0};
   GXValidFnc[38]={ id: 38, fld:"",grid:0};
   GXValidFnc[39]={ id: 39, fld:"BTN_ENTER",grid:0,evt:"e11054_client",std:"ENTER"};
   GXValidFnc[40]={ id: 40, fld:"",grid:0};
   GXValidFnc[41]={ id: 41, fld:"BTN_CANCEL",grid:0,evt:"e12054_client"};
   GXValidFnc[42]={ id: 42, fld:"",grid:0};
   GXValidFnc[43]={ id: 43, fld:"BTN_DELETE",grid:0,evt:"e18054_client",std:"DELETE"};
   GXValidFnc[44]={ id: 44, fld:"PROMPT_11",grid:4};
   this.A11Plan070TrnId = 0 ;
   this.Z11Plan070TrnId = 0 ;
   this.O11Plan070TrnId = 0 ;
   this.A11Plan070TrnId = 0 ;
   this.Events = {"e11054_client": ["ENTER", true] ,"e12054_client": ["CANCEL", true]};
   this.EvtParms["ENTER"] = [[{"postForm":true}],[]];
   this.EvtParms["REFRESH"] = [[],[]];
   this.EvtParms["VALID_PLAN070TRNID"] = [[{"av":"A11Plan070TrnId","fld":"PLAN070TRNID","pic":"ZZZ9"},{"av":"Gx_mode","fld":"vMODE","pic":"@!"}],[{"av":"Gx_mode","fld":"vMODE","pic":"@!"},{"av":"Z11Plan070TrnId"},{"ctrl":"BTN_DELETE","prop":"Enabled"},{"ctrl":"BTN_ENTER","prop":"Enabled"}]];
   this.setPrompt("PROMPT_11", [34]);
   this.EnterCtrl = ["BTN_ENTER"];
   this.Initialize( );
});
gx.wi( function() { gx.createParentObj(this.plan070trn);});
