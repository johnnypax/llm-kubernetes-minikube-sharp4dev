---
title: "Scaling sicuro di un Deployment"
slug: "safe-scaling-deployment"
tags: ["scaling","deployment","hpa"]
severity: "low"
allowed_namespaces: ["dev","staging", "sharp4dev", "test-ns-giovanni"]
tool_hints:
  - tool: "cluster_context"
    when: "Controllo stato cluster prima dello scaling"
  - tool: "list_pods"
    when: "Verificare readiness dei pod esistenti"
  - tool: "scale_deployment"
    when: "Applicare il nuovo numero di repliche"
risk_notes: "Non superare +2 repliche rispetto all'ultimo stato senza approvazione."
updated_at: "2025-09-17"
---

## Obiettivo
Modificare le repliche di un Deployment in modo controllato.

## Procedura sintetica
1. **Pre-check**: `cluster_context` per stato sintetico; verificare che il namespace sia ammesso.
2. **Verifica readiness**: `list_pods` nel namespace ? readiness ? 90%.
3. **Applica scaling**: `scale_deployment` a `replicas ? 10`, incremento massimo +2.
4. **Post-check**: `list_pods` ? tutte le nuove repliche in Ready entro 5 minuti.

## Rollback
- Riportare le repliche al valore precedente se readiness < 90% entro 5 min.
