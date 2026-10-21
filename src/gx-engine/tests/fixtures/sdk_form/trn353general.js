gx.evt.autoSkip = false;
gx.define('trn353general', true, function (CmpContext) {
   this.ServerClass =  "trn353general" ;
   this.PackageName =  "GeneXus.Programs" ;
   this.ServerFullClass =  "trn353general.aspx" ;
   this.setObjectType("web");
   this.setCmpContext(CmpContext);
   this.ReadonlyForm = true;
   this.hasEnterEvent = false;
   this.skipOnEnter = false;
   this.autoRefresh = true;
   this.fullAjax = true;
   this.supportAjaxEvents =  true ;
   this.ajaxSecurityToken =  true ;
   this.DSO =  "KBTeste" ;
   this.SetStandaloneVars=function()
   {
   };
   this.Valid_Trn353id=function()
   {
      return this.validCliEvt("Valid_Trn353id", 0, function () {
      try {
         var gxballoon = gx.util.balloon.getNew("TRN353ID");
         this.AnyError  = 0;

      }
      catch(e){}
      try {
          if (gxballoon == null) return true; return gxballoon.show();
      }
      catch(e){}
      return true ;
      });
   }
   this.e111v1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         this.call("trn353.aspx", ["UPD", this.A61Trn353Id], null, ["Mode","Trn353Id"]);
         this.refreshOutputs([]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e121v1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         this.call("trn353.aspx", ["DLT", this.A61Trn353Id], null, ["Mode","Trn353Id"]);
         this.refreshOutputs([]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e151v2_client=function()
   {
      return this.executeServerEvent("ENTER", true, null, false, false);
   };
   this.e161v2_client=function()
   {
      return this.executeServerEvent("CANCEL", true, null, false, false);
   };
   this.GXValidFnc = [];
   var GXValidFnc = this.GXValidFnc ;
   this.GXCtrlIds=[2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18];
   this.GXLastCtrlId =18;
   GXValidFnc[2]={ id: 2, fld:"",grid:0};
   GXValidFnc[3]={ id: 3, fld:"MAINTABLE",grid:0};
   GXValidFnc[4]={ id: 4, fld:"",grid:0};
   GXValidFnc[5]={ id: 5, fld:"",grid:0};
   GXValidFnc[6]={ id: 6, fld:"",grid:0};
   GXValidFnc[7]={ id: 7, fld:"",grid:0};
   GXValidFnc[8]={ id: 8, fld:"BTNUPDATE",grid:0,evt:"e111v1_client"};
   GXValidFnc[9]={ id: 9, fld:"",grid:0};
   GXValidFnc[10]={ id: 10, fld:"BTNDELETE",grid:0,evt:"e121v1_client"};
   GXValidFnc[11]={ id: 11, fld:"",grid:0};
   GXValidFnc[12]={ id: 12, fld:"",grid:0};
   GXValidFnc[13]={ id: 13, fld:"ATTRIBUTESTABLE",grid:0};
   GXValidFnc[14]={ id: 14, fld:"",grid:0};
   GXValidFnc[15]={ id: 15, fld:"",grid:0};
   GXValidFnc[16]={ id: 16, fld:"",grid:0};
   GXValidFnc[17]={ id: 17, fld:"",grid:0};
   GXValidFnc[18]={ id:18 ,lvl:0,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:1,grid:0,gxgrid:null,fnc:this.Valid_Trn353id,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"TRN353ID",fmt:0,gxz:"Z61Trn353Id",gxold:"O61Trn353Id",gxvar:"A61Trn353Id",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.A61Trn353Id=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z61Trn353Id=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("TRN353ID",gx.O.A61Trn353Id,0)},c2v:function(){if(this.val()!==undefined)gx.O.A61Trn353Id=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("TRN353ID",',')},nac:gx.falseFn};
   this.A61Trn353Id = 0 ;
   this.Events = {"e151v2_client": ["ENTER", true] ,"e161v2_client": ["CANCEL", true] ,"e111v1_client": ["'DOUPDATE'", false] ,"e121v1_client": ["'DODELETE'", false]};
   this.EvtParms["REFRESH"] = [[{"av":"A61Trn353Id","fld":"TRN353ID","pic":"ZZZ9"}],[]];
   this.EvtParms["'DOUPDATE'"] = [[{"av":"A61Trn353Id","fld":"TRN353ID","pic":"ZZZ9"}],[]];
   this.EvtParms["'DODELETE'"] = [[{"av":"A61Trn353Id","fld":"TRN353ID","pic":"ZZZ9"}],[]];
   this.EvtParms["ENTER"] = [[],[]];
   this.EvtParms["VALID_TRN353ID"] = [[],[]];
   this.Initialize( );
});
