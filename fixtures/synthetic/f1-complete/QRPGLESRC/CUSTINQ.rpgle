**free
// Synthetic customer inquiry. FIXTURE-CANARY-RPG
ctl-opt dftactgrp(*no) actgrp(*caller);

dcl-f CUSTMAS keyed usage(*input);

dcl-pi *n;
  custId packed(7:0) const;
end-pi;

chain custId CUSTMAS;
