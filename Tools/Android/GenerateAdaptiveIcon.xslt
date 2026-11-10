<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0"
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:svg="http://www.w3.org/2000/svg"
    xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:aapt="http://schemas.android.com/aapt"
    exclude-result-prefixes="svg">
  <xsl:import href="../Branding/SvgPathGeometry.xslt" />
  <xsl:output method="xml" encoding="utf-8" indent="yes" />
  <xsl:key name="gradient" match="svg:linearGradient" use="@id" />

  <xsl:template match="/svg:svg">
    <xsl:variable name="caseColors" select="key('gradient', 'case')/svg:stop" />
    <xsl:variable name="vault" select="svg:g[@id = 'vault']" />
    <xsl:if test="@viewBox != '0 0 256 256' or count($caseColors) != 2 or not($vault/*)">
      <xsl:message terminate="yes">The app icon SVG must have a 256-unit square viewBox, a two-stop case gradient, and a vault group.</xsl:message>
    </xsl:if>

    <adaptive-icon>
      <background>
        <shape android:shape="rectangle">
          <gradient android:angle="270" android:startColor="{$caseColors[1]/@stop-color}"
                     android:endColor="{$caseColors[2]/@stop-color}" />
        </shape>
      </background>
      <foreground>
        <!-- Center the SVG's 256-unit artboard in a 360-unit viewport.
             The vault, including its outline, occupies 64.2dp of the 66dp safe zone. -->
        <vector android:width="108dp" android:height="108dp"
                android:viewportWidth="360" android:viewportHeight="360">
          <group android:translateX="52" android:translateY="52">
            <xsl:apply-templates select="$vault/*" />
          </group>
        </vector>
      </foreground>
    </adaptive-icon>
  </xsl:template>

  <xsl:template match="svg:path | svg:circle | svg:rect">
    <path>
      <xsl:attribute name="android:pathData">
        <xsl:apply-templates select="." mode="geometry" />
      </xsl:attribute>
      <xsl:if test="@stroke">
        <xsl:attribute name="android:strokeColor"><xsl:value-of select="@stroke" /></xsl:attribute>
        <xsl:attribute name="android:strokeWidth"><xsl:value-of select="@stroke-width" /></xsl:attribute>
      </xsl:if>
      <xsl:choose>
        <xsl:when test="starts-with(@fill, 'url(#')">
          <xsl:variable name="fill" select="key('gradient', substring-before(substring-after(@fill, '#'), ')'))" />
          <xsl:if test="not($fill[@gradientUnits = 'userSpaceOnUse'])">
            <xsl:message terminate="yes">Android icon foreground gradients must use user-space coordinates.</xsl:message>
          </xsl:if>
          <aapt:attr name="android:fillColor">
            <gradient android:type="linear" android:startX="{$fill/@x1}" android:startY="{$fill/@y1}"
                      android:endX="{$fill/@x2}" android:endY="{$fill/@y2}">
              <xsl:for-each select="$fill/svg:stop">
                <item android:offset="{@offset}" android:color="{@stop-color}" />
              </xsl:for-each>
            </gradient>
          </aapt:attr>
        </xsl:when>
        <xsl:when test="@fill != 'none'">
          <xsl:attribute name="android:fillColor"><xsl:value-of select="@fill" /></xsl:attribute>
        </xsl:when>
      </xsl:choose>
    </path>
  </xsl:template>

  <xsl:template match="svg:g[@id = 'vault']/*" priority="-0.25">
    <xsl:message terminate="yes">Unsupported shape in the app icon vault group: <xsl:value-of select="local-name()" />.</xsl:message>
  </xsl:template>
</xsl:stylesheet>
