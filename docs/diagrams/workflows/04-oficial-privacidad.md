# 04 · Oficial de privacidad de la ARL (`oficial-privacidad`)

**Quién es:** el responsable de protección de datos de la ARL (Ley 1581/2012). Responde ante la SIC, la Superfinanciera y MinTrabajo.

**Permisos:** `auditoria:read`, `auditoria:export`, `auditoria:revisar`, `solicitudes-hc:gestionar`.

**Lo que no hace:** no abre casos (no tiene `casos:read`). Los radicados y los ids de caso los ve como texto, no como enlaces.

**Usuario de desarrollo:** `privacidad.arla`.

## Mapa de navegación

```mermaid
flowchart LR
    INI["Inicio<br/>revisiones pendientes,<br/>solicitudes abiertas"]:::pantalla
    SOL["Solicitudes de copia de HC"]:::pantalla
    ACC["Auditoría: accesos"]:::pantalla
    REV["Auditoría: revisiones de emergencia"]:::pantalla
    CTA["Cuenta"]:::pantalla
    INI --> SOL
    INI --> ACC
    INI --> REV
    INI --> CTA
    REV -- "ver los accesos del caso o del actor" --> ACC

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
```

## Flujo A: revisar un acceso de emergencia (72 h)

```mermaid
flowchart TD
    R0["Un profesional usó el acceso de emergencia"]:::sistema --> R1["Webhook auditoria.acceso-emergencia al core de la ARL<br/>+ una revisión PENDIENTE en Halsa"]:::sistema
    R1 --> R2["Revisiones de emergencia:<br/>ordenadas por vencimiento (72 h)"]:::pantalla
    R2 --> R3["Lee el motivo que escribió el profesional"]:::accion
    R3 --> R4["Auditoría → accesos filtrados<br/>por caso y por actor"]:::pantalla
    R4 --> R5{"¿Fue una urgencia real?"}
    R5 -- sí --> R6["Cerrar como JUSTIFICADO"]:::accion
    R5 -- no --> R7["Cerrar como INJUSTIFICADO<br/>+ observaciones"]:::riesgo
    R7 --> R8["Acciones disciplinarias o legales<br/>(fuera del sistema)"]:::externo

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
    classDef externo fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:2 2
```

```mermaid
stateDiagram-v2
    [*] --> PENDIENTE: acceso de emergencia (vence a las 72 h)
    PENDIENTE --> CERRADA: conclusión JUSTIFICADO o INJUSTIFICADO
    CERRADA --> [*]
```

## Flujo B: atender una solicitud de copia de la historia clínica

```mermaid
flowchart TD
    H0["El trabajador la pide desde el portal"]:::sistema --> H1["Solicitud RECIBIDA.<br/>Se abre SOLICITUD_COPIA_HC (10 días hábiles) ⏱"]:::plazo
    H1 --> H2["Solicitudes de copia de HC → Recibidas"]:::pantalla
    H2 --> H3["Pasar a trámite"]:::accion --> H4["EN TRÁMITE"]:::pantalla
    H4 --> H5["Prepara y entrega la copia por el medio elegido<br/>(correo certificado, electrónico o presencial)"]:::externo
    H5 --> H6{"¿Se pudo entregar?"}
    H6 -- sí --> H7["Cerrar como ENTREGADA + nota"]:::accion
    H6 -- no --> H8["Cerrar como RECHAZADA + nota<br/>(el trabajador ve la nota)"]:::riesgo
    H7 --> H9["El plazo queda CUMPLIDO"]:::sistema
    H8 --> H9
    H2 -. "también puede cerrarse<br/>directamente desde RECIBIDA" .-> H6

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef plazo fill:#ffb30033,stroke:#ffb300,stroke-width:1.5px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
    classDef externo fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:2 2
```

```mermaid
stateDiagram-v2
    [*] --> RECIBIDA: el paciente la solicita
    RECIBIDA --> EN_TRAMITE: tramitar
    RECIBIDA --> ENTREGADA: cerrar
    RECIBIDA --> RECHAZADA: cerrar
    EN_TRAMITE --> ENTREGADA: cerrar
    EN_TRAMITE --> RECHAZADA: cerrar
    ENTREGADA --> [*]
    RECHAZADA --> [*]
```

## Flujo C: investigar accesos

```mermaid
flowchart LR
    I1["Auditoría: accesos"]:::pantalla --> I2["Filtra: fechas, caso, actor,<br/>acción (Leer, Crear, Corregir…),<br/>resultado (Éxito, Denegado…)"]:::accion
    I2 --> I3["Revisa los DENEGADO:<br/>la mejor señal de fisgoneo"]:::riesgo
    I3 --> I4["Abre el detalle: IP, navegador, cliente,<br/>nivel de autenticación, motivo"]:::pantalla

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
```

- El registro de auditoría es inmutable y se sella cada 5 minutos con una raíz Merkle.
- Exportarlo (`auditoria:export`) aún **no tiene endpoint**.
