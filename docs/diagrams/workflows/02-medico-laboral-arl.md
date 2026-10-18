# 02 · Médico laboral de la ARL (`medico-laboral`)

**Quién es:** el médico de salud ocupacional de la ARL que lleva la historia clínica del caso.

**Permisos:** `casos:read`, `casos:write`, `incapacidades:read`, `incapacidades:write`, `plazos:read`, `clinico:read`, `clinico:write`.

**Condiciones:** todo lo clínico 🔒 exige estar **asignado al caso** (lo asigna el analista) y la **verificación reforzada** (contraseña + código OTP).

> Todavía no existe un usuario de desarrollo con este rol. El calificador (`calificador.arla`) tiene todos sus permisos y sirve para probar estos flujos.

## Mapa de navegación

```mermaid
flowchart LR
    INI["Inicio"]:::pantalla --> CAS["Casos"]:::pantalla --> RES["Resumen"]:::pantalla
    INI --> PLA["Plazos<br/>(solo lectura)"]:::pantalla
    RES --- EQU["Equipo y remisiones<br/>(puede remitir)"]:::pantalla
    RES --- PCA["Plazos del caso<br/>(solo lectura)"]:::pantalla
    RES --- INC["Incapacidades<br/>+ cargar 🔒"]:::clinico
    RES --- HC["Historia clínica 🔒"]:::clinico
    HC --> REG["Detalle del registro"]:::clinico
    HC --> NUE["Nuevo registro o corrección"]:::clinico

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
```

## Flujo de trabajo: historia clínica

```mermaid
flowchart TD
    S0["El analista lo asigna al caso"]:::sistema --> S1["Abre el caso → Historia clínica"]:::pantalla
    S1 --> S2{"¿Verificación reforzada<br/>activa (acr=2)?"}
    S2 -- no --> S3["Ingresa su código OTP<br/>(ventana emergente)"]:::accion --> S4
    S2 -- sí --> S4{"¿Está asignado al caso?"}
    S4 -- no --> S5["403: no asignado.<br/>Pide al analista que lo asigne"]:::riesgo
    S4 -- sí --> S6["Lee los registros<br/>(queda auditado: LEER)"]:::clinico

    S6 --> S7["REPORTE_INICIAL<br/>(lesión y descripción del FURAT)"]:::clinico
    S6 --> S8["Conceptos que envió la IPS"]:::clinico
    S6 --> N1["Nuevo registro"]:::accion
    N1 --> N2["Tipo: concepto médico, evolución<br/>o concepto de rehabilitación"]:::clinico
    N2 --> N3["Fecha de atención (no futura) +<br/>motivo, anamnesis, examen, análisis, plan +<br/>diagnósticos CIE-10"]:::clinico
    N3 --> N4{"¿El caso admite registros?"}
    N4 -- "rechazado o cerrado" --> N5["409 Clinico.CasoNoAdmiteRegistros"]:::riesgo
    N4 -- sí --> N6["Registro cifrado y guardado.<br/>Webhook concepto.registrado (sin contenido)"]:::sistema

    S6 --> K1["¿Hay un error en un registro?"]:::accion
    K1 --> K2["Corregir: se crea un registro NUEVO<br/>que apunta al original (corrigeA).<br/>Nada se borra ni se edita"]:::clinico

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
```

## Flujo de trabajo: cargar una incapacidad

```mermaid
sequenceDiagram
    autonumber
    actor M as Médico
    participant UI as Backoffice
    participant API as API Halsa
    participant AV as Antivirus
    participant W as Worker

    M->>UI: Incapacidades → "Cargar incapacidad"
    UI->>UI: Verificación reforzada ANTES del formulario (acr=2)
    M->>UI: Certificado (inicial o prórroga), fecha de inicio, días (1–30),<br/>diagnóstico CIE-10, IPS emisora, modalidad y PDF (≤10 MB)
    UI->>API: POST /casos/{id}/incapacidades (multipart + Idempotency-Key)
    API->>AV: Escanea el PDF
    alt PDF infectado o inválido
        API-->>UI: 415 / 422
    else Certificado repetido
        API-->>UI: 409 Incapacidades.CertificadoDuplicado
    else OK
        API-->>UI: 201 + alertas (150, 180 o 540 días, carga extemporánea)
        Note over API: El diagnóstico y el PDF se guardan cifrados.<br/>La respuesta solo trae datos administrativos
        API-)W: incapacidad.registrada
        W->>W: Recalcula TOPE_IT_180 de la cadena ⏱
    end
    UI-->>M: Muestra las alertas (p. ej. "180 días: requiere concepto de rehabilitación")
```

## Reglas útiles

- **Reintentos:** si la red falla, reintentar con la misma `Idempotency-Key` no duplica la incapacidad.
- **Prórrogas:** una prórroga se encadena a la incapacidad anterior del mismo caso. Los días se acumulan por cadena.
- **Descargar el soporte PDF** también es un acceso clínico auditado: exige OTP y asignación.
