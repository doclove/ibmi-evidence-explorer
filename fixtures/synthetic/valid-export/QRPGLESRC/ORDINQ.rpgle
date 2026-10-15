**free
// Synthetic order inquiry program. FIXTURE-CANARY-RPG
ctl-opt dftactgrp(*no) actgrp(*caller);

dcl-f ORDHDR keyed usage(*input);

dcl-pi *n;
  orderId packed(9:0) const;
end-pi;

chain orderId ORDHDR;
if %found(ORDHDR);
  dsply ('Order status: ' + OHSTAT);
endif;

*inlr = *on;
