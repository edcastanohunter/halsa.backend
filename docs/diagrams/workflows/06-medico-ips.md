# 06 · Médico de la IPS (`medico-ips`)

**Quién es:** el médico tratante en la IPS a la que la ARL remitió el caso.

**Permisos:** `casos:read`, `clinico:read`, `clinico:write`, `incapacidades:read`, `incapacidades:write`.

**No ve:** los plazos legales ni el dictamen PCL.

**Usuario de desarrollo:** `medico.ips` (tiene OTP).

**Para tocar algo clínico 🔒 necesita tres cosas a la vez:**

1. una **remisión vigente** del caso hacia su IPS;
2. estar **asignado** por su coordinador;
3. la **verificación reforzada** (OTP).

## Mapa de navegación

```mermaid
flowchart LR
    INI["Inicio"]:::pantalla --> CAS["Casos<br/>(remitidos a su IPS)"]:::pantalla --> RES["Resumen"]:::pantalla
    RES --- EQU["Equipo<br/>(solo lectura)"]:::pantalla
    RES --- INC["Incapacidades<br/>+ cargar 🔒"]:::clinico
    RES --- HC["Historia clínica 🔒"]:::clinico
    INI --> CTA["Cuenta<br/>(su identificador para que lo asignen)"]:::pantalla

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
```

## Flujo de trabajo: la atención del paciente

```mermaid
flowchart TD
    M0["La ARL remite y el coordinador lo asigna"]:::sistema --> M1["Casos → abre el caso"]:::pantalla
    M1 --> M2["Historia clínica: OTP si hace falta"]:::accion
    M2 --> M3["Lee el reporte inicial y los conceptos previos 🔒"]:::clinico
    M3 --> M4["Atiende al paciente"]:::externo
    M4 --> M5["Registra un concepto médico, una evolución<br/>o un concepto de rehabilitación 🔒"]:::clinico
    M4 --> M6{"¿Requiere incapacidad?"}
    M6 -- sí --> M7["Carga la incapacidad + el PDF del certificado 🔒<br/>(la IPS emisora viene precargada con su REPS)"]:::clinico
    M7 --> M8["Alertas de días acumulados.<br/>La ARL recibe incapacidad.registrada"]:::sistema
    M6 -- "prórroga" --> M9["Tipo PRÓRROGA, encadenada<br/>a la incapacidad anterior"]:::clinico --> M8
    M5 --> M10["La ARL recibe concepto.registrado<br/>(solo ids, nunca el contenido)"]:::sistema

    M0 -. "la ARL revoca la remisión" .-> X1["Pierde el acceso al caso (404)"]:::riesgo
    M1 -. "sin asignación" .-> X2["403: no asignado.<br/>Pide al coordinador que lo asigne"]:::riesgo

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
    classDef externo fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:2 2
```

Lo mismo puede llegar **de máquina a máquina** desde el sistema de historia clínica de la IPS: conceptos e incapacidades por `/v1/integraciones/*`, con mTLS (ver [09-integraciones.md](09-integraciones.md)). El médico usa el backoffice cuando la IPS no tiene esa integración.
